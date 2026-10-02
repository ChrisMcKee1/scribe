import Foundation

/// Keep number lexemes and reject duplicate members: Foundation's decoder alone accepts both 1.0 and repeated keys.
enum LibraryEditsJSON {
    static func read(libraryID: String, data: Data) -> BuiltInEditsReadResult {
        func result(_ state: LibraryFileState, _ version: Int?, _ edits: BuiltInLibraryEdits? = nil)
            -> BuiltInEditsReadResult
        {
            BuiltInEditsReadResult(state: state, edits: edits, version: version)
        }
        var parser = Parser(bytes: Array(data))
        guard let root = try? parser.document(), case .object(let object) = root,
            case .number(let literal) = object["version"],
            literal.range(of: #"^-?[0-9]+$"#, options: .regularExpression) != nil
        else { return result(.unreadable, nil) }
        let version = Int(literal)
        if version == nil && !literal.hasPrefix("-") || (version ?? 0) > 1 {
            return result(.newer, version)
        }
        guard version == 1, case .text(let library) = object["library"],
            LibraryTermKey.areSame(library, libraryID), case .array(let terms) = object["terms"]
        else { return result(.unreadable, version) }
        var edits: [BuiltInTermEdit] = []
        var keys = Set<LibraryTermKey>()
        var broken = false
        var newer = false
        for item in terms {
            guard case .object(let term) = item else {
                broken = true
                continue
            }
            if case .text(let name) = term["intent"], BuiltInTermIntent(rawValue: name) == nil {
                newer = true
                continue
            }
            guard case .text(let rawKey) = term["key"], case .text(let name) = term["intent"],
                let intent = BuiltInTermIntent(rawValue: name)
            else {
                broken = true
                continue
            }
            let key = LibraryTermKey.from(rawKey)
            let base = values(term["base"])
            let value = values(term["value"])
            let acknowledged = values(term["acknowledged"])
            let edit = BuiltInTermEdit(
                key: key.value, intent: intent, base: base, value: value, acknowledged: acknowledged)
            let malformedValues = ["base", "value", "acknowledged"].contains {
                if let node = term[$0], node != .null { return values(node) == nil }
                return false
            }
            guard !key.isEmpty, keys.insert(key).inserted, BuiltInLibraryOverlay.valid(edit), !malformedValues else {
                broken = true
                continue
            }
            edits.append(edit)
        }
        if newer { return result(.newer, version) }
        if broken { return result(.unreadable, version) }
        return result(.available, version, BuiltInLibraryEdits(version: 1, library: library, terms: edits))
    }

    private static func values(_ node: Node?) -> TermValues? {
        guard case .object(let object) = node,
            case .text(let spoken) = object["spoken"], case .text(let written) = object["written"],
            case .flag(let wholeWord) = object["wholeWord"], case .flag(let enabled) = object["enabled"]
        else { return nil }
        return TermValues(spoken, written, wholeWord, enabled)
    }

    private indirect enum Node: Equatable {
        case object([String: Node])
        case array([Node])
        case text(String)
        case number(String)
        case flag(Bool)
        case null
        case invalid
    }

    private struct Parser {
        let bytes: [UInt8]
        var position = 0

        mutating func document() throws -> Node {
            if bytes.starts(with: [0xef, 0xbb, 0xbf]) { position = 3 }
            let node = try value(depth: 0)
            whitespace()
            guard position == bytes.count else { throw WordPackError.invalidEdits }
            return node
        }

        mutating func value(depth: Int) throws -> Node {
            whitespace()
            guard depth < 64, position < bytes.count else { throw WordPackError.invalidEdits }
            switch bytes[position] {
            case 123:
                position += 1
                whitespace()
                var object: [String: Node] = [:]
                if take(125) { return .object(object) }
                repeat {
                    whitespace()
                    guard case .text(let key) = try string(), object[key] == nil else {
                        throw WordPackError.invalidEdits
                    }
                    whitespace()
                    guard take(58) else { throw WordPackError.invalidEdits }
                    object[key] = try value(depth: depth + 1)
                    whitespace()
                    if take(125) { return .object(object) }
                    guard take(44) else { throw WordPackError.invalidEdits }
                } while true
            case 91:
                position += 1
                whitespace()
                var array: [Node] = []
                if take(93) { return .array(array) }
                repeat {
                    array.append(try value(depth: depth + 1))
                    whitespace()
                    if take(93) { return .array(array) }
                    guard take(44) else { throw WordPackError.invalidEdits }
                } while true
            case 34: return try string()
            case 116: return try keyword("true", node: .flag(true))
            case 102: return try keyword("false", node: .flag(false))
            case 110: return try keyword("null", node: .null)
            default:
                let start = position
                while position < bytes.count && ![9, 10, 13, 32, 44, 93, 125].contains(bytes[position]) {
                    position += 1
                }
                let literal = String(decoding: bytes[start..<position], as: UTF8.self)
                guard
                    literal.range(
                        of: #"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$"#,
                        options: .regularExpression) != nil
                else { throw WordPackError.invalidEdits }
                return .number(literal)
            }
        }

        mutating func string() throws -> Node {
            let start = position
            guard take(34) else { throw WordPackError.invalidEdits }
            while position < bytes.count {
                let byte = bytes[position]
                position += 1
                if byte == 34 {
                    let data = Data(bytes[start..<position])
                    return (try? JSONDecoder().decode(String.self, from: data)).map(Node.text) ?? .invalid
                }
                if byte == 92 {
                    guard position < bytes.count else { throw WordPackError.invalidEdits }
                    let escaped = bytes[position]
                    position += 1
                    if escaped == 117 {
                        guard position + 4 <= bytes.count else { throw WordPackError.invalidEdits }
                        let digits = bytes[position..<(position + 4)]
                        guard
                            digits.allSatisfy({
                                (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0)
                            })
                        else { throw WordPackError.invalidEdits }
                        position += 4
                    } else if ![34, 47, 92, 98, 102, 110, 114, 116].contains(escaped) {
                        throw WordPackError.invalidEdits
                    }
                } else if byte < 32 {
                    throw WordPackError.invalidEdits
                }
            }
            throw WordPackError.invalidEdits
        }

        mutating func keyword(_ text: String, node: Node) throws -> Node {
            let token = Array(text.utf8)
            guard bytes.dropFirst(position).starts(with: token) else { throw WordPackError.invalidEdits }
            position += token.count
            return node
        }

        mutating func whitespace() {
            while position < bytes.count && [9, 10, 13, 32].contains(bytes[position]) { position += 1 }
        }

        mutating func take(_ byte: UInt8) -> Bool {
            guard position < bytes.count, bytes[position] == byte else { return false }
            position += 1
            return true
        }
    }
}
