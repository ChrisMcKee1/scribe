import Foundation

/// Latency/volume statistics computed from stored dictation history: the numbers behind the
/// Diagnostics performance panel. Direct port of Windows' `Scribe.Core.Diagnostics.DictationStats`
/// (decode time, cleanup time, real-time factor come from per-dictation history, so no separate
/// telemetry is collected).
enum DictationStats {
    struct MetricSummary: Equatable {
        let average: Double
        let min: Double
        let max: Double
        let p50: Double
        let p95: Double
    }

    /// Aggregated view of the dictations inside the window. `nil` when there were none.
    struct Snapshot: Equatable {
        let count: Int
        let totalAudioSeconds: Double
        let decodeCount: Int
        let decodeMs: MetricSummary?
        let cleanupCount: Int
        let cleanupMs: MetricSummary?
        let combinedCount: Int
        let combinedMs: MetricSummary?
        let fastestRtf: Double
        let rtfP50: Double
        let rtfP95: Double
        let longestAudioSeconds: Double
    }

    /// Computes stats over the entries newer than `since`. Entries with a non-positive audio
    /// length are skipped (RTF would be undefined). Returns `nil` when nothing qualifies, so the
    /// panel can show a friendly empty state instead of zeros.
    static func compute(entries: [DictationHistoryRecord], since: Date) -> Snapshot? {
        var decodeMsValues: [Double] = []
        var rtfValues: [Double] = []
        var cleanupMsValues: [Double] = []
        var combinedMsValues: [Double] = []
        var count = 0
        var totalAudioMs = 0.0
        var longestAudioMs = 0.0

        for entry in entries {
            guard entry.startedAt >= since, entry.audioMilliseconds > 0 else {
                continue
            }

            count += 1

            if let decodeMs = entry.decodeMilliseconds {
                decodeMsValues.append(decodeMs)
                rtfValues.append(decodeMs / entry.audioMilliseconds)
            }

            if let cleanupMs = entry.cleanupMilliseconds, cleanupMs > 0 {
                cleanupMsValues.append(cleanupMs)
                combinedMsValues.append((entry.decodeMilliseconds ?? 0) + cleanupMs)
            }

            totalAudioMs += entry.audioMilliseconds
            longestAudioMs = max(longestAudioMs, entry.audioMilliseconds)
        }

        guard totalAudioMs > 0 else {
            return nil
        }

        decodeMsValues.sort()
        rtfValues.sort()
        cleanupMsValues.sort()
        combinedMsValues.sort()

        return Snapshot(
            count: count,
            totalAudioSeconds: totalAudioMs / 1000.0,
            decodeCount: decodeMsValues.count,
            decodeMs: decodeMsValues.isEmpty ? nil : summarize(decodeMsValues),
            cleanupCount: cleanupMsValues.count,
            cleanupMs: cleanupMsValues.isEmpty ? nil : summarize(cleanupMsValues),
            combinedCount: combinedMsValues.count,
            combinedMs: combinedMsValues.isEmpty ? nil : summarize(combinedMsValues),
            fastestRtf: rtfValues.first(where: { $0 > 0 }) ?? 0,
            rtfP50: rtfValues.isEmpty ? 0 : percentile(rtfValues, 0.50),
            rtfP95: rtfValues.isEmpty ? 0 : percentile(rtfValues, 0.95),
            longestAudioSeconds: longestAudioMs / 1000.0)
    }

    private static func summarize(_ sortedAscending: [Double]) -> MetricSummary {
        let average = sortedAscending.reduce(0, +) / Double(sortedAscending.count)
        return MetricSummary(
            average: average,
            min: sortedAscending[0],
            max: sortedAscending[sortedAscending.count - 1],
            p50: percentile(sortedAscending, 0.50),
            p95: percentile(sortedAscending, 0.95))
    }

    /// Linear-interpolation percentile over an ascending-sorted list (the R-7 / Excel method).
    /// With a handful of samples this reads sensibly: the P95 of 3 dictations is near the max, not
    /// an arbitrary bucket edge.
    static func percentile(_ sortedAscending: [Double], _ p: Double) -> Double {
        precondition(!sortedAscending.isEmpty, "At least one sample is required.")

        if sortedAscending.count == 1 {
            return sortedAscending[0]
        }

        let clampedP = min(max(p, 0), 1)
        let rank = clampedP * Double(sortedAscending.count - 1)
        let lower = Int(rank.rounded(.down))
        let upper = Int(rank.rounded(.up))
        if lower == upper {
            return sortedAscending[lower]
        }

        let weight = rank - Double(lower)
        return sortedAscending[lower] + (sortedAscending[upper] - sortedAscending[lower]) * weight
    }
}

/// How fast speech recognition ran, in the Settings glossary's words: "times faster than real time", never a
/// real-time factor or a percentile. A port of Windows' `TryDictationTiming.SpeedLabel`.
enum DictationSpeedText {
    /// "20 times faster than real time" for a real-time factor of 0.05, at least "1 times"; empty for a factor that
    /// is not a positive, finite number.
    static func speedLabel(realTimeFactor: Double) -> String {
        guard realTimeFactor > 0, realTimeFactor.isFinite else {
            return ""
        }
        let faster = max(1, min((1 / realTimeFactor).rounded(.toNearestOrAwayFromZero), 1_000_000_000))
        return "\(Int(faster).formatted()) times faster than real time"
    }

    /// The Diagnostics line: the fastest dictation, the typical one, and the speed 19 in 20 dictations reach. A
    /// factor that says nothing leaves its sentence out.
    static func summary(fastest: Double, typical: Double, slowest: Double) -> String {
        var sentences: [String] = []
        let best = speedLabel(realTimeFactor: fastest)
        if !best.isEmpty {
            sentences.append("Fastest: \(best).")
        }
        let usual = speedLabel(realTimeFactor: typical)
        if !usual.isEmpty {
            sentences.append("Typical: \(usual).")
        }
        let most = speedLabel(realTimeFactor: slowest)
        if !most.isEmpty {
            sentences.append("19 in 20: \(most) or more.")
        }
        return sentences.joined(separator: " ")
    }
}
