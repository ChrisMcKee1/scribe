import Foundation

/// A calendar date with no time-of-day or timezone component, mirroring the role of C#'s
/// `DateOnly` in Windows' `UsageAnalyzer`. All arithmetic is performed against a fixed UTC
/// calendar once a date's year/month/day are resolved from a `Date` + `TimeZone`, matching
/// `DateOnly`'s timezone-agnostic-after-construction semantics.
struct LocalDate: Hashable, Comparable {
    let year: Int
    let month: Int
    let day: Int

    private static let utcCalendar: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        return calendar
    }()

    init(year: Int, month: Int, day: Int) {
        self.year = year
        self.month = month
        self.day = day
    }

    init(date: Date, timeZone: TimeZone) {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let components = calendar.dateComponents([.year, .month, .day], from: date)
        self.year = components.year ?? 1
        self.month = components.month ?? 1
        self.day = components.day ?? 1
    }

    /// A linear day count (arbitrary epoch), usable only for differencing two `LocalDate`s.
    var dayNumber: Int {
        var components = DateComponents()
        components.year = year
        components.month = month
        components.day = day
        let date = Self.utcCalendar.date(from: components) ?? Date(timeIntervalSince1970: 0)
        return Int((date.timeIntervalSince1970 / 86_400).rounded())
    }

    func addingDays(_ days: Int) -> LocalDate {
        var components = DateComponents()
        components.year = year
        components.month = month
        components.day = day + days
        let date = Self.utcCalendar.date(from: components) ?? Date(timeIntervalSince1970: 0)
        let resolved = Self.utcCalendar.dateComponents([.year, .month, .day], from: date)
        return LocalDate(year: resolved.year ?? 1, month: resolved.month ?? 1, day: resolved.day ?? 1)
    }

    /// The Monday on or before this date, mirroring Windows' `StartOfWeek`.
    func startOfWeek() -> LocalDate {
        var components = DateComponents()
        components.year = year
        components.month = month
        components.day = day
        let date = Self.utcCalendar.date(from: components) ?? Date(timeIntervalSince1970: 0)
        let weekday = Self.utcCalendar.component(.weekday, from: date) // 1 = Sunday ... 7 = Saturday
        let offset = (weekday + 5) % 7 // Monday-based offset, matching C#'s DayOfWeek arithmetic
        return addingDays(-offset)
    }

    static func < (lhs: LocalDate, rhs: LocalDate) -> Bool {
        (lhs.year, lhs.month, lhs.day) < (rhs.year, rhs.month, rhs.day)
    }
}

/// Computes descriptive, local-only usage metrics from retained dictation history. A faithful
/// Swift port of Windows' `Scribe.Core.Diagnostics.UsageAnalyzer`, including its trend-bucketing,
/// top-apps ranking, and covered/novel term-mining logic, so `UsageAnalyzerTests`' fixtures
/// reproduce here.
enum UsageAnalyzer {
    struct AppUsage: Equatable {
        let name: String
        let dictations: Int
        let words: Int
    }

    enum TrendGranularity {
        case daily
        case weekly
    }

    struct TrendPoint: Equatable {
        let start: LocalDate
        let dictations: Int
        let words: Int
    }

    struct TermUsage: Equatable {
        let text: String
        let dictations: Int
        let occurrences: Int
        let covered: Bool
    }

    struct Snapshot {
        let dictations: Int
        let words: Int
        let activeDays: Int
        let speechSeconds: Double
        let averageWords: Double
        let topApps: [AppUsage]
        let trend: [TrendPoint]
        let terms: [TermUsage]
        let granularity: TrendGranularity
    }

    /// One dictation, scoped to what usage analysis needs (a subset of `DictationHistoryRecord`,
    /// mirroring Windows' `HistoryEntry`).
    struct Entry {
        let timestampUtc: Date
        let text: String
        let audioMilliseconds: Double
        let targetApp: String?
    }

    /// Computes one internally consistent snapshot. Every metric uses entries on or after
    /// `sinceUtc` (and on or before `nowUtc`); callers own the newest-first read cap and its
    /// disclosure.
    static func compute(
        entries: [Entry],
        knownTerms: [DictionaryEntry],
        sinceUtc: Date,
        nowUtc: Date,
        timeZone: TimeZone = .current,
        maxApps: Int = 8,
        maxTerms: Int = 16
    ) -> Snapshot {
        let selected = entries.filter { $0.timestampUtc >= sinceUtc && $0.timestampUtc <= nowUtc }
        let wordCounts = selected.map { countWords($0.text) }
        let words = wordCounts.reduce(0, +)
        let activeDays = Set(selected.map { LocalDate(date: $0.timestampUtc, timeZone: timeZone) }).count

        var appBuckets: [String: (displayName: String, dictations: Int, words: Int)] = [:]
        for (index, entry) in selected.enumerated() {
            let rawName = entry.targetApp?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            let name = rawName.isEmpty ? "Unknown app" : rawName
            let key = name.lowercased()
            var bucket = appBuckets[key] ?? (displayName: name, dictations: 0, words: 0)
            // Windows keeps the ordinally-smallest original-cased spelling seen for the group
            // (`OrderBy(entry.TargetApp, Ordinal).First()`).
            if name < bucket.displayName {
                bucket.displayName = name
            }
            bucket.dictations += 1
            bucket.words += wordCounts[index]
            appBuckets[key] = bucket
        }
        let topApps = appBuckets.values
            .map { AppUsage(name: $0.displayName, dictations: $0.dictations, words: $0.words) }
            .sorted { lhs, rhs in
                if lhs.dictations != rhs.dictations { return lhs.dictations > rhs.dictations }
                return lhs.name.localizedCaseInsensitiveCompare(rhs.name) == .orderedAscending
            }
            .prefix(max(0, maxApps))

        let (trend, granularity) = buildTrend(
            entries: selected, wordCounts: wordCounts, sinceUtc: sinceUtc, nowUtc: nowUtc, timeZone: timeZone)

        return Snapshot(
            dictations: selected.count,
            words: words,
            activeDays: activeDays,
            speechSeconds: selected.reduce(0.0) { $0 + max(0, $1.audioMilliseconds) } / 1_000.0,
            averageWords: selected.isEmpty ? 0 : Double(words) / Double(selected.count),
            topApps: Array(topApps),
            trend: trend,
            terms: extractTerms(entries: selected, knownTerms: knownTerms, maxTerms: maxTerms),
            granularity: granularity)
    }

    /// Counts Unicode letter/number words without assuming a particular language.
    static func countWords(_ text: String) -> Int {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return 0 }
        return wordRegex.numberOfMatches(in: text, range: NSRange(location: 0, length: (text as NSString).length))
    }

    private static func buildTrend(
        entries: [Entry],
        wordCounts: [Int],
        sinceUtc: Date,
        nowUtc: Date,
        timeZone: TimeZone
    ) -> ([TrendPoint], TrendGranularity) {
        let end = LocalDate(date: nowUtc, timeZone: timeZone)
        let requestedStart = LocalDate(date: sinceUtc, timeZone: timeZone)
        let firstEntry = entries.isEmpty
            ? end
            : entries.map { LocalDate(date: $0.timestampUtc, timeZone: timeZone) }.min()!
        var start = requestedStart.year <= 1 ? firstEntry : requestedStart
        if start > end {
            start = end
        }

        let granularity: TrendGranularity = end.dayNumber - start.dayNumber > 31 ? .weekly : .daily
        let useWeeks = granularity == .weekly
        if useWeeks {
            start = start.startOfWeek()
        }

        var grouped: [LocalDate: (dictations: Int, words: Int)] = [:]
        for (index, entry) in entries.enumerated() {
            let date = LocalDate(date: entry.timestampUtc, timeZone: timeZone)
            let bucket = useWeeks ? date.startOfWeek() : date
            var value = grouped[bucket] ?? (0, 0)
            value.dictations += 1
            value.words += wordCounts[index]
            grouped[bucket] = value
        }

        var points: [TrendPoint] = []
        var cursor = start
        while cursor <= end {
            let value = grouped[cursor] ?? (0, 0)
            points.append(TrendPoint(start: cursor, dictations: value.dictations, words: value.words))
            cursor = cursor.addingDays(useWeeks ? 7 : 1)
        }
        return (points, granularity)
    }

    // MARK: - Term extraction

    private struct KnownTerm {
        let canonical: String
        let forms: [String]
    }

    private static func extractTerms(
        entries: [Entry],
        knownTerms: [DictionaryEntry],
        maxTerms: Int
    ) -> [TermUsage] {
        var groups: [String: [DictionaryEntry]] = [:]
        var groupOrder: [String] = []
        for entry in knownTerms where entry.enabled {
            let replacement = entry.replacement.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !replacement.isEmpty else { continue }
            let key = replacement.lowercased()
            if groups[key] == nil {
                groupOrder.append(key)
            }
            groups[key, default: []].append(entry)
        }

        var known: [KnownTerm] = []
        for key in groupOrder {
            guard let groupEntries = groups[key], let first = groupEntries.first else { continue }
            let canonical = first.replacement.trimmingCharacters(in: .whitespacesAndNewlines)
            var seenForms = Set<String>()
            var forms: [String] = []
            for entry in groupEntries {
                for candidate in [
                    entry.pattern.trimmingCharacters(in: .whitespacesAndNewlines),
                    entry.replacement.trimmingCharacters(in: .whitespacesAndNewlines),
                ] where candidate.count >= 2 {
                    let lowered = candidate.lowercased()
                    if seenForms.insert(lowered).inserted {
                        forms.append(candidate)
                    }
                }
            }
            // A 1-char pattern with a 1-char replacement leaves no usable forms; skipping keeps
            // the max-over-forms lookup below from ever seeing an empty form list.
            guard !forms.isEmpty else { continue }
            known.append(KnownTerm(canonical: canonical, forms: forms))
        }

        // Forms Token() can represent whole are counted through one tokenization pass and hash
        // lookups; only multi-token phrases retain compiled regex matching.
        var singleTokenForms: [String: String] = [:] // lowercased form -> canonical-cased form
        var phraseMatchers: [(text: String, regex: NSRegularExpression)] = []
        var seenFormKeys = Set<String>()
        for term in known {
            for form in term.forms {
                let key = form.lowercased()
                guard seenFormKeys.insert(key).inserted else { continue }
                if isSingleTokenForm(form) {
                    singleTokenForms[key] = form
                } else if let regex = try? NSRegularExpression(
                    pattern: phrasePattern(for: form), options: [.caseInsensitive]) {
                    phraseMatchers.append((text: form, regex: regex))
                }
            }
        }

        var coveredForms = Set<String>()
        for term in known {
            for form in term.forms {
                coveredForms.insert(form.lowercased())
            }
        }

        var termDictations = [Int](repeating: 0, count: known.count)
        var termOccurrences = [Int](repeating: 0, count: known.count)
        var novelForms: [String: (surface: String, dictations: Int, occurrences: Int)] = [:]

        for entry in entries {
            let nsText = entry.text as NSString
            let fullRange = NSRange(location: 0, length: nsText.length)
            var formCounts: [String: Int] = [:] // keyed by canonical form text
            var lastMatchEnds: [String: Int] = [:]
            var seenNovelForms = Set<String>()

            let tokenMatches = tokenRegex.matches(in: entry.text, range: fullRange)
            for match in tokenMatches {
                let tokenRange = match.range
                let token = nsText.substring(with: tokenRange)
                countSingleTokenForms(
                    token: token,
                    tokenStart: tokenRange.location,
                    singleTokenForms: singleTokenForms,
                    formCounts: &formCounts,
                    lastMatchEnds: &lastMatchEnds)

                var trimmed = token
                while let last = trimmed.last, ".,:;!?".contains(last) {
                    trimmed.removeLast()
                }
                let lowered = trimmed.lowercased()
                guard trimmed.count >= 2, !coveredForms.contains(lowered),
                      DictionarySuggestionMiner.isJargonShaped(trimmed) else {
                    continue
                }

                var current = novelForms[lowered] ?? (surface: trimmed, dictations: 0, occurrences: 0)
                if seenNovelForms.insert(lowered).inserted {
                    current.dictations += 1
                }
                current.occurrences += 1
                novelForms[lowered] = current
            }

            for matcher in phraseMatchers {
                let count = matcher.regex.numberOfMatches(in: entry.text, range: fullRange)
                if count > 0 {
                    formCounts[matcher.text] = count
                }
            }

            for (index, term) in known.enumerated() {
                var count = 0
                for form in term.forms {
                    count = max(count, formCounts[form] ?? 0)
                }
                if count > 0 {
                    termDictations[index] += 1
                    termOccurrences[index] += count
                }
            }
        }

        var results: [TermUsage] = []
        for (index, term) in known.enumerated() where termDictations[index] > 0 {
            results.append(TermUsage(
                text: term.canonical,
                dictations: termDictations[index],
                occurrences: termOccurrences[index],
                covered: true))
        }
        for value in novelForms.values where value.dictations >= 2 {
            results.append(TermUsage(
                text: value.surface, dictations: value.dictations, occurrences: value.occurrences, covered: false))
        }

        return results
            .sorted { lhs, rhs in
                if lhs.dictations != rhs.dictations { return lhs.dictations > rhs.dictations }
                if lhs.occurrences != rhs.occurrences { return lhs.occurrences > rhs.occurrences }
                return lhs.text.localizedCaseInsensitiveCompare(rhs.text) == .orderedAscending
            }
            .prefix(max(0, maxTerms))
            .map { $0 }
    }

    private static func isSingleTokenForm(_ form: String) -> Bool {
        let nsForm = form as NSString
        let range = NSRange(location: 0, length: nsForm.length)
        guard let match = tokenRegex.firstMatch(in: form, range: range) else { return false }
        return match.range.location == 0 && match.range.length == nsForm.length
    }

    /// Counts every occurrence of a known single-token form inside `token`, scanning every
    /// substring start/end so a form need not be the whole token (e.g. "next" inside "next.js"),
    /// while never double-counting overlapping matches of the *same* form. Mirrors Windows'
    /// `CountSingleTokenForms`.
    private static func countSingleTokenForms(
        token: String,
        tokenStart: Int,
        singleTokenForms: [String: String],
        formCounts: inout [String: Int],
        lastMatchEnds: inout [String: Int]
    ) {
        let nsToken = token as NSString
        let length = nsToken.length
        for start in 0..<length {
            if start > 0, isLetterOrDigit(nsToken.character(at: start - 1)) {
                continue
            }

            for end in stride(from: start + 2, through: length, by: 1) {
                if end < length, isLetterOrDigit(nsToken.character(at: end)) {
                    continue
                }

                let candidate = nsToken.substring(with: NSRange(location: start, length: end - start))
                guard let form = singleTokenForms[candidate.lowercased()] else { continue }

                let absoluteStart = tokenStart + start
                if let lastEnd = lastMatchEnds[form], absoluteStart < lastEnd {
                    continue
                }

                formCounts[form, default: 0] += 1
                lastMatchEnds[form] = tokenStart + end
            }
        }
    }

    private static func isLetterOrDigit(_ utf16Char: unichar) -> Bool {
        guard let scalar = Unicode.Scalar(utf16Char) else { return false }
        return Character(scalar).isLetter || Character(scalar).isNumber
    }

    private static func phrasePattern(for phrase: String) -> String {
        "(?<![\\p{L}\\p{N}])" + NSRegularExpression.escapedPattern(for: phrase) + "(?![\\p{L}\\p{N}])"
    }

    private static let wordRegex = try! NSRegularExpression(
        pattern: "[\\p{L}\\p{M}\\p{N}]+(?:['\u{2019}\\-][\\p{L}\\p{M}\\p{N}]+)*")

    private static let tokenRegex = try! NSRegularExpression(
        pattern: "\\.?[\\p{L}\\p{N}][\\p{L}\\p{M}\\p{N}._#+\\-/]*")
}
