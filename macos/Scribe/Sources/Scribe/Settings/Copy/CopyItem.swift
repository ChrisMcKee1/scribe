import Foundation

/// Why a Mac string differs from the Windows string it comes from, or why it has no Windows twin.
/// Every `CopyItem` that is not word for word the Windows text carries one of these, so the difference is a
/// recorded decision and the manifest test can tell a deliberate change from a drift.
enum CopyDeviation: String, CaseIterable, Sendable {
    /// "this PC" becomes "this Mac", and other names for the machine.
    case thisMac
    /// A Windows setting, menu or place that has a different Mac name (System Settings, menu bar, Open at Login).
    case macSystemFeature
    /// Key names, key glyphs and the shortcut defaults of the Mac.
    case macKeys
    /// Foundry Local is installed by the person on the Mac, so the Windows "Scribe manages it" wording is not true.
    case foundryLocal
    /// GitHub Copilot is not offered on the Mac, and the copy says so plainly.
    case copilotUnavailable
    /// API keys and secrets live in the Keychain, not the database.
    case keychain
    /// The Windows text makes a claim that was found stale or wrong; the Mac text states the checked fact.
    case staleWindowsCorrected
    /// The Mac does something the Windows text does not describe, or does it differently.
    case macBehaviour
    /// Text with no Windows twin, because the Mac needs a sentence Windows never had.
    case macOnly
    /// A control that applies at once on the Mac (a sheet or an action), so its words differ.
    case appliesAtOnce
    /// The Windows string is a template the Mac fills in differently.
    case macTemplate
    /// The feature exists only on Windows, so the string is not carried over.
    case windowsOnly
    /// The Mac applies this when the whole window is saved, as its staged window decides.
    case stagedSave

    /// The sentence recorded for the deviation, shown in the report and in failing tests.
    var reason: String {
        switch self {
        case .thisMac: return "The machine is a Mac."
        case .macSystemFeature: return "The Mac has its own name for this system feature."
        case .macKeys: return "Mac key names and defaults differ from Windows."
        case .foundryLocal: return "The person installs Foundry Local, so Scribe does not manage it."
        case .copilotUnavailable: return "GitHub Copilot is not available on the Mac."
        case .keychain: return "Secrets are kept in the Keychain."
        case .staleWindowsCorrected: return "The Windows wording was found stale or wrong and is corrected."
        case .macBehaviour: return "The Mac behaves differently from Windows here."
        case .macOnly: return "Windows has no equivalent text."
        case .appliesAtOnce: return "This control applies at once on the Mac."
        case .macTemplate: return "The Mac fills this template in with its own values."
        case .windowsOnly: return "The feature exists only on Windows."
        case .stagedSave: return "The Mac applies this when you save, as the staged window decides."
        }
    }
}

/// One string a person reads in Scribe's Settings, the menu bar, a notice or the recording indicator.
///
/// `text` is what the Mac shows. It may hold `{name}` placeholders, which `render` fills in. `windows` is the
/// Windows string it was ported from, kept word for word so tests can check it against the Windows sources;
/// it is nil for text with no Windows twin. Catalog text is plain ASCII with three dots for an ellipsis, which
/// `render` turns into the real character.
struct CopyItem: Equatable, Sendable {
    let id: String
    let text: String
    let windows: String?
    let deviation: CopyDeviation?

    /// The Windows text, word for word.
    static func same(_ id: String, _ text: String) -> CopyItem {
        CopyItem(id: id, text: text, windows: text, deviation: nil)
    }

    /// Windows text that the Mac changes, for the recorded reason.
    static func changed(_ id: String, _ text: String, windows: String, because deviation: CopyDeviation) -> CopyItem {
        CopyItem(id: id, text: text, windows: windows, deviation: deviation)
    }

    /// Text that has no Windows twin.
    static func added(_ id: String, _ text: String, because deviation: CopyDeviation = .macOnly) -> CopyItem {
        CopyItem(id: id, text: text, windows: nil, deviation: deviation)
    }

    /// The names of the `{placeholders}` in the text, in order of first use.
    var placeholders: [String] {
        var names: [String] = []
        var current: String?
        for character in text {
            if character == "{" {
                current = ""
            } else if character == "}" {
                if let name = current, !name.isEmpty, !names.contains(name) {
                    names.append(name)
                }
                current = nil
            } else if current != nil {
                current?.append(character)
            }
        }
        return names
    }

    /// The text a person reads: placeholders filled in and "..." turned into an ellipsis. The template is read once,
    /// left to right, so a value is inserted exactly as given (it may hold braces or three dots) and the order of
    /// `values` never matters. A placeholder with no value is left as written.
    func render(_ values: [String: String] = [:]) -> String {
        var result = ""
        var literal = ""
        var name: String?
        func flushLiteral() {
            result += literal.replacingOccurrences(of: "...", with: "\u{2026}")
            literal = ""
        }
        for character in text {
            if let open = name {
                if character == "}" {
                    flushLiteral()
                    result += values[open] ?? "{" + open + "}"
                    name = nil
                } else {
                    name = open + String(character)
                }
            } else if character == "{" {
                name = ""
            } else {
                literal.append(character)
            }
        }
        if let open = name {
            literal += "{" + open
        }
        flushLiteral()
        return result
    }}

/// A page's (or surface's) strings. The items are the stored `CopyItem` properties, so a catalog cannot list a
/// string the manifest does not see, and the manifest cannot hold one a catalog does not have.
protocol CopyCatalog: Sendable {
    /// The prefix every id in the catalog starts with, such as `dictation`.
    var prefix: String { get }
}

extension CopyCatalog {
    /// Every `CopyItem` the catalog holds, in declaration order.
    var items: [CopyItem] {
        var found: [CopyItem] = []
        for child in Mirror(reflecting: self).children {
            if let item = child.value as? CopyItem {
                found.append(item)
            } else if let list = child.value as? [CopyItem] {
                found.append(contentsOf: list)
            }
        }
        return found
    }
}
