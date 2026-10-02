import Foundation

/// The one place numbers, plurals, sizes, durations, dates and lists become text for Settings, so every page reads
/// the same and follows the person's language and region. Windows hard codes English ("{n:N0} words"); here the
/// numbers, sizes, durations and dates follow the locale, and only the English plural rule is built in, with a
/// `localized` hook for another language's catalog later.
enum SettingsFormat {
    /// A whole number with the locale's grouping.
    static func number(_ value: Int, locale: Locale = .current) -> String {
        value.formatted(.number.locale(locale))
    }

    /// A count and its noun: "1 word", "2 words", "1,200 words".
    static func quantity(_ count: Int, one: String, other: String, locale: Locale = .current) -> String {
        number(count, locale: locale) + " " + (count == 1 ? one : other)
    }

    /// "31 words", or "1 word".
    static func words(_ count: Int, locale: Locale = .current) -> String {
        quantity(count, one: "word", other: "words", locale: locale)
    }

    /// A size on disk or in memory, in decimal units as Finder shows them: "1.5 MB".
    static func byteSize(_ bytes: Int64, locale: Locale = .current) -> String {
        let style = ByteCountFormatStyle(
            style: .file, allowedUnits: .all, spellsOutZero: false, includesActualByteCount: false, locale: locale)
        return bytes.formatted(style)
    }

    /// A length of time in whole words: "30 seconds", "10 minutes", "1 hour".
    static func duration(seconds: Int, locale: Locale = .current) -> String {
        let allowed: Set<Duration.UnitsFormatStyle.Unit> =
            seconds >= 3600 ? [.hours, .minutes] : (seconds >= 60 ? [.minutes, .seconds] : [.seconds])
        let style = Duration.UnitsFormatStyle(allowedUnits: allowed, width: .wide).locale(locale)
        return Duration.seconds(seconds).formatted(style)
    }

    /// A date and time as the person's region writes them.
    static func dateTime(_ date: Date, locale: Locale = .current, timeZone: TimeZone = .current) -> String {
        let style = Date.FormatStyle(date: .abbreviated, time: .shortened, locale: locale, timeZone: timeZone)
        return date.formatted(style)
    }

    /// Names joined the way the Windows window does in English ("A, B and C"), and by the locale's own rule
    /// elsewhere.
    static func list(_ names: [String], locale: Locale = .current) -> String {
        if locale.language.languageCode?.identifier == "en" {
            switch names.count {
            case 0: return ""
            case 1: return names[0]
            default: return names.dropLast().joined(separator: ", ") + " and " + (names.last ?? "")
            }
        }
        return names.formatted(.list(type: .and, width: .standard).locale(locale))
    }
}
