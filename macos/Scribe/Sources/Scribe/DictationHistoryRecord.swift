import Foundation

/// One row of dictation history, mirroring Windows' `HistoryEntry` shape closely enough for
/// `DictationStats.compute` to be a faithful port. macOS has a single ASR backend today (no
/// model-catalog concept yet), so there is no `transcriptionModelId` filter here; every row with
/// a decode time counts toward the decode/RTF stats.
struct DictationHistoryRecord {
    let startedAt: Date
    let durationSeconds: Double
    let sampleCount: Int
    let decodeMilliseconds: Double?
    let cleanupMilliseconds: Double?
    /// The final, post-processed transcript text, when recorded. Older rows (pre-history-mining
    /// schema) or captures with no resampled audio surface as `nil`. Feeds
    /// `DictionarySuggestionMiner`, mirroring Windows' `HistoryEntry.Text`.
    let transcriptText: String?

    init(
        startedAt: Date,
        durationSeconds: Double,
        sampleCount: Int,
        decodeMilliseconds: Double? = nil,
        cleanupMilliseconds: Double? = nil,
        transcriptText: String? = nil
    ) {
        self.startedAt = startedAt
        self.durationSeconds = durationSeconds
        self.sampleCount = sampleCount
        self.decodeMilliseconds = decodeMilliseconds
        self.cleanupMilliseconds = cleanupMilliseconds
        self.transcriptText = transcriptText
    }

    /// Audio duration in milliseconds, mirroring Windows' `AudioMilliseconds`.
    var audioMilliseconds: Double {
        durationSeconds * 1000.0
    }
}
