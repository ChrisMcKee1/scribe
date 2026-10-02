import Foundation

enum LibraryNaming {
    private static let asciiHyphen = UnicodeScalar(0x2D)!

    static let customIDPrefix = "custom-"
    static let newLibraryBaseName = "New word pack"
    static let importedLibraryBaseName = "Imported word pack"

    static func slug(_ name: String?) -> String {
        let value = (name ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        var result = String.UnicodeScalarView()
        var pendingDash = false

        for scalar in value.unicodeScalars {
            if CharacterSet.alphanumerics.contains(scalar) {
                if pendingDash, !result.isEmpty {
                    result.append(asciiHyphen)
                }
                appendLowerInvariant(scalar, to: &result)
                pendingDash = false
            } else {
                pendingDash = true
            }
        }

        let slug = String(result)
        return slug.isEmpty ? "library" : slug
    }

    static func newCustomID<S: Sequence>(name: String?, takenIDs: S) -> String where S.Element == String {
        unique(candidate: customIDPrefix + slug(name), takenIDs: takenIDs)
    }

    static func remapID<S: Sequence>(stem: String, takenIDs: S) -> String where S.Element == String {
        unique(candidate: customIDPrefix + stem, takenIDs: takenIDs)
    }

    static func uniqueName<S: Sequence>(_ baseName: String?, takenNames: S) -> String where S.Element == String {
        let committed = committedName(baseName).isEmpty ? "Word pack" : committedName(baseName)
        let taken = Set(
            takenNames.compactMap {
                let name = committedName($0)
                return name.isEmpty ? nil : name.lowercased()
            })

        guard !taken.contains(committed.lowercased()) else {
            var index = 2
            while true {
                let candidate = "\(committed) \(index)"
                if !taken.contains(candidate.lowercased()) {
                    return candidate
                }
                index += 1
            }
        }

        return committed
    }

    private static func committedName(_ value: String?) -> String {
        (value ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func unique<S: Sequence>(candidate: String, takenIDs: S) -> String where S.Element == String {
        let taken = Set(
            takenIDs.compactMap {
                let trimmed = $0.trimmingCharacters(in: .whitespacesAndNewlines)
                return trimmed.isEmpty ? nil : trimmed.lowercased()
            })

        guard !taken.contains(candidate.lowercased()) else {
            var index = 2
            while true {
                let suffixed = "\(candidate)-\(index)"
                if !taken.contains(suffixed.lowercased()) {
                    return suffixed
                }
                index += 1
            }
        }

        return candidate
    }

    private static func appendLowerInvariant(_ scalar: UnicodeScalar, to result: inout String.UnicodeScalarView) {
        if scalar.value == 0x0130 {
            result.append(scalar)
            return
        }

        for lowered in String(scalar).lowercased().unicodeScalars {
            result.append(lowered)
        }
    }
}
