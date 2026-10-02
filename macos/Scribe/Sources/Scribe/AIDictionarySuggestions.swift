import Combine
import Foundation

struct AIDictionarySuggestionAccess: Sendable {
    var loadEntries: @Sendable () async throws -> [DictionaryEntry]
    var addIfAbsent: @Sendable ([DictionaryEntry]) async throws -> [DictionaryEntry]

    static func live(_ store: PersistenceStore) -> AIDictionarySuggestionAccess {
        AIDictionarySuggestionAccess(
            loadEntries: { try await store.loadAllDictionaryEntries() },
            addIfAbsent: { try await store.addDictionaryEntriesIfAbsent($0) })
    }
}

/// The completion must use the cleanup service's saved-recipient handoff and use/release barrier. It must verify
/// `recipient` for every outbound attempt, including retries, and honor task cancellation. A raw provider call does
/// not satisfy this contract and must not be wired here.
struct AIDictionarySuggestionService {
    typealias Completion =
        @Sendable (
            _ systemPrompt: String, _ userMessage: String, _ recipient: CleanupConnection
        ) async throws -> String

    let currentRecipient: @MainActor @Sendable () -> CleanupConnection?
    let isCleanupEnabled: @MainActor @Sendable () -> Bool
    let complete: Completion
}

struct AIDictionarySuggestionConsent: Identifiable {
    let id: UUID
    let message: String

    fileprivate let recipient: CleanupConnection
    fileprivate let reportID: UInt64
    fileprivate let sample: String
}

/// Opt-in AI suggestions use only the raw transcript from the volatile Playground report. They never read history,
/// whose text is post-processed and may contain a snippet expansion or template-like replacement.
@MainActor
final class AIDictionarySuggestionModel: ObservableObject {
    static let maxSampleCharacters = 6_000
    static let maxSuggestions = 25

    static let systemPrompt =
        "You help build a personal dictation dictionary. Find technical terms, product names, acronyms, and jargon "
        + "in the user's raw speech-recognition transcript that a recognizer may spell incorrectly, mis-case, or "
        + "mishear. Return only a JSON array of objects with exactly the keys \"spoken\" and \"written\". "
        + "\"spoken\" is the lowercase phrase the recognizer may produce; \"written\" is the correct form. "
        + "Only suggest terms present in the transcript. Skip ordinary words and uncertain guesses. Do not return "
        + "templates, signatures, or multi-line text. Return at most 25 suggestions."

    @Published private(set) var consent: AIDictionarySuggestionConsent?
    @Published private(set) var suggestions: [DictionaryEntry] = []
    @Published private(set) var selectedSpokenForms: Set<String> = []
    @Published private(set) var isRequesting = false
    @Published private(set) var isCommitting = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var statusMessage: String?
    @Published private(set) var isReviewPresented = false
    private(set) var inFlight: Task<Void, Never>?

    private let rawReport: @MainActor @Sendable () -> (id: UInt64, text: String)?
    private let access: AIDictionarySuggestionAccess
    private let service: AIDictionarySuggestionService?
    private let onChanged: @MainActor @Sendable () -> Void
    private var requestID: UInt64 = 0
    private var activeReportID: UInt64?
    private var requestRecipient: CleanupConnection?
    private var reviewRecipient: CleanupConnection?
    private var observation: SettingsNotificationObservation?
    private var reportObservation: AnyCancellable?

    init(
        reports: PipelineReportStore,
        access: AIDictionarySuggestionAccess,
        service: AIDictionarySuggestionService? = nil,
        center: NotificationCenter = .default,
        onChanged: @escaping @MainActor @Sendable () -> Void = {}
    ) {
        rawReport = {
            guard let report = reports.latest, let rawText = report.rawText else { return nil }
            return (report.dictationID, rawText)
        }
        self.access = access
        self.service = service
        self.onChanged = onChanged
        reportObservation = reports.$latest.sink { [weak self] report in
            MainActor.assumeIsolated {
                self?.invalidateIfSampleChanged(report?.dictationID)
            }
        }
        observation = SettingsNotificationObservation(UserDefaults.didChangeNotification, center: center) {
            [weak self] in
            self?.invalidateIfRecipientChanged()
        }
    }

    var canSuggest: Bool {
        guard let service, service.isCleanupEnabled(), service.currentRecipient() != nil,
            let report = rawReport(),
            Self.boundedRawSample(report.text) != nil
        else {
            return false
        }
        return true
    }

    var hasBoundCompletion: Bool {
        service != nil
    }

    var canCommitSelection: Bool {
        !isCommitting && !selectedSpokenForms.isEmpty
    }

    static func boundedRawSample(_ rawText: String?, maxCharacters: Int = maxSampleCharacters) -> String? {
        guard maxCharacters > 0,
            let text = rawText?.trimmingCharacters(in: .whitespacesAndNewlines),
            !text.isEmpty
        else {
            return nil
        }
        return String(text.prefix(maxCharacters))
    }

    static func consentMessage(sampleCharacters: Int, destination: String) -> String {
        "Scribe will send up to \(sampleCharacters.formatted()) characters from the latest raw text recognized "
            + "before dictionary, word pack, and snippet processing, plus suggestion instructions, to \(destination). "
            + "The cleanup service may also receive applicable dictionary and permitted word pack vocabulary for "
            + "this request. Snippet templates are never included. Review suggestions before adding them."
    }

    /// Captures the currently served recipient and raw in-memory report before presenting consent. No unsaved cleanup
    /// settings draft is consulted. The recipient and report are checked again when the user confirms.
    func prepareConsent() {
        guard !isRequesting, !isCommitting, let service,
            service.isCleanupEnabled(),
            let recipient = service.currentRecipient(),
            let report = rawReport(),
            let sample = Self.boundedRawSample(report.text)
        else {
            errorMessage =
                "AI suggestions are unavailable until cleanup is on and a safe saved-provider handoff is ready."
            return
        }

        errorMessage = nil
        statusMessage = nil
        consent = AIDictionarySuggestionConsent(
            id: UUID(),
            message: Self.consentMessage(
                sampleCharacters: sample.count,
                destination: Self.destination(for: recipient)),
            recipient: recipient,
            reportID: report.id,
            sample: sample)
        activeReportID = report.id
        suggestions = []
        selectedSpokenForms = []
        reviewRecipient = nil
    }

    /// Called only by the affirmative consent action. The injected completion owns the per-send recipient checks and
    /// release/use barrier. This model checks before and after the operation and never publishes a stale answer.
    func requestSuggestions() {
        guard !isRequesting, !isCommitting, let consent, let service,
            service.isCleanupEnabled(), service.currentRecipient() == consent.recipient,
            rawReport()?.id == consent.reportID
        else {
            self.consent = nil
            errorMessage = "AI cleanup changed before the request could start. Nothing was sent. Try again."
            return
        }

        self.consent = nil
        requestID &+= 1
        let attempt = requestID
        requestRecipient = consent.recipient
        isRequesting = true
        errorMessage = nil
        statusMessage = nil
        let complete = service.complete
        let access = self.access
        inFlight = Task { [weak self] in
            guard let self else { return }
            do {
                try Task.checkCancellation()
                let response = try await complete(Self.systemPrompt, consent.sample, consent.recipient)
                try Task.checkCancellation()
                guard self.isCurrent(attempt, recipient: consent.recipient, reportID: consent.reportID) else {
                    return
                }
                let existing = try await access.loadEntries()
                try Task.checkCancellation()
                guard self.isCurrent(attempt, recipient: consent.recipient, reportID: consent.reportID) else {
                    return
                }
                let parsed = Self.parseSuggestions(response, existing: existing)
                self.inFlight = nil
                self.requestRecipient = nil
                self.isRequesting = false
                guard !parsed.isEmpty else {
                    self.activeReportID = nil
                    self.statusMessage = "The AI service returned no new usable suggestions."
                    return
                }
                self.suggestions = parsed
                self.selectedSpokenForms = Set(parsed.map { Self.spokenFormKey($0.pattern) })
                self.reviewRecipient = consent.recipient
                self.isReviewPresented = true
            } catch is CancellationError {
                self.finishCancelled(attempt)
            } catch {
                guard self.isCurrent(attempt, recipient: consent.recipient, reportID: consent.reportID) else {
                    return
                }
                self.finishFailed(attempt)
            }
        }
    }

    func toggleSelection(for entry: DictionaryEntry, selected: Bool) {
        let key = Self.spokenFormKey(entry.pattern)
        if selected {
            selectedSpokenForms.insert(key)
        } else {
            selectedSpokenForms.remove(key)
        }
    }

    /// Writes only the entries selected in the review sheet. Persistence rechecks spoken forms against the stored
    /// dictionary in its transaction, so an intervening edit or import cannot create duplicate rules.
    func commitSelectedSuggestions() async {
        guard canCommitSelection else { return }
        let selected = suggestions.filter { selectedSpokenForms.contains(Self.spokenFormKey($0.pattern)) }
        isCommitting = true
        defer { isCommitting = false }
        do {
            let added = try await access.addIfAbsent(selected)
            suggestions = []
            selectedSpokenForms = []
            isReviewPresented = false
            activeReportID = nil
            reviewRecipient = nil
            statusMessage =
                added.isEmpty
                ? "Those spoken forms are already in your dictionary."
                : "Added \(added.count) reviewed \(added.count == 1 ? "word" : "words") to your dictionary."
            if !added.isEmpty {
                onChanged()
            }
        } catch {
            errorMessage = "Couldn't add the reviewed suggestions to your dictionary."
        }
    }

    func cancelConsent() {
        consent = nil
        if !isRequesting, !isReviewPresented {
            activeReportID = nil
        }
    }

    func dismissReview() {
        suggestions = []
        selectedSpokenForms = []
        isReviewPresented = false
        activeReportID = nil
        reviewRecipient = nil
    }

    func cancel() {
        requestID &+= 1
        inFlight?.cancel()
        inFlight = nil
        requestRecipient = nil
        isRequesting = false
        activeReportID = nil
        consent = nil
        dismissReview()
    }

    static func parseSuggestions(
        _ response: String?,
        existing: [DictionaryEntry],
        maxSuggestions: Int = maxSuggestions
    ) -> [DictionaryEntry] {
        guard maxSuggestions > 0, let json = extractArray(response),
            let raw = try? JSONDecoder().decode([RawSuggestion].self, from: Data(json.utf8))
        else {
            return []
        }

        var known = Set(existing.map { spokenFormKey($0.pattern) }.filter { !$0.isEmpty })
        var result: [DictionaryEntry] = []
        for suggestion in raw {
            guard let spoken = normalize(suggestion.spoken),
                let written = normalize(suggestion.written),
                spoken.count <= 80, written.count <= 80,
                spoken.caseInsensitiveCompare(written) != .orderedSame,
                spoken == spoken.lowercased(),
                !known.contains(spokenFormKey(spoken))
            else {
                continue
            }
            known.insert(spokenFormKey(spoken))
            result.append(DictionaryEntry(pattern: spoken, replacement: written))
            if result.count == maxSuggestions { break }
        }
        return result
    }

    private func isCurrent(_ attempt: UInt64, recipient: CleanupConnection, reportID: UInt64) -> Bool {
        guard requestID == attempt, let service else { return false }
        guard service.isCleanupEnabled(), service.currentRecipient() == recipient,
            requestRecipient == recipient, rawReport()?.id == reportID
        else {
            invalidateForSettingsChange()
            errorMessage =
                "AI cleanup or the raw dictation sample changed while suggestions were requested. The reply was discarded."
            return false
        }
        return true
    }

    private func finishCancelled(_ attempt: UInt64) {
        guard requestID == attempt else { return }
        inFlight = nil
        requestRecipient = nil
        isRequesting = false
        activeReportID = nil
    }

    private func finishFailed(_ attempt: UInt64) {
        guard requestID == attempt else { return }
        inFlight = nil
        requestRecipient = nil
        isRequesting = false
        activeReportID = nil
        errorMessage = "Couldn't get AI dictionary suggestions. No suggestion was added."
    }

    private func invalidateIfSampleChanged(_ reportID: UInt64?) {
        guard let activeReportID, activeReportID != reportID else { return }
        invalidateForSettingsChange()
        errorMessage =
            "The recent dictation changed while suggestions were being prepared. The request or result was discarded."
    }

    private func invalidateIfRecipientChanged() {
        guard let service else {
            cancel()
            return
        }
        let expected = consent?.recipient ?? requestRecipient ?? reviewRecipient
        guard let expected else { return }
        guard service.isCleanupEnabled(), service.currentRecipient() == expected else {
            invalidateForSettingsChange()
            errorMessage =
                "AI cleanup changed while suggestions were being prepared. The request or result was discarded."
            return
        }
    }

    private func invalidateForSettingsChange() {
        requestID &+= 1
        inFlight?.cancel()
        inFlight = nil
        requestRecipient = nil
        isRequesting = false
        activeReportID = nil
        consent = nil
        dismissReview()
    }

    private static func destination(for recipient: CleanupConnection) -> String {
        switch recipient.target {
        case .foundryLocal:
            return "Foundry Local on this Mac"
        case .ollama:
            return "Ollama on this Mac"
        case .microsoftFoundry:
            return "Microsoft Foundry"
        case .openAICompatible(let serviceURL, _, _, _):
            return LocalAiServer.appAt(serviceURL.absoluteString) == .none
                ? "your configured OpenAI-compatible service"
                : "a local AI service on this Mac"
        }
    }

    private static func spokenFormKey(_ value: String) -> String {
        value.trimmingCharacters(in: .whitespacesAndNewlines).folding(
            options: [.caseInsensitive, .diacriticInsensitive], locale: Locale(identifier: "en_US_POSIX"))
    }

    private static func normalize(_ value: String?) -> String? {
        guard let value, !value.isEmpty,
            !value.unicodeScalars.contains(where: {
                CharacterSet.controlCharacters.contains($0) || $0.value == 0x2013 || $0.value == 0x2014
            })
        else {
            return nil
        }
        let normalized = value.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        guard !normalized.isEmpty else { return nil }
        return normalized
    }

    private static func extractArray(_ response: String?) -> String? {
        guard let response, let start = response.firstIndex(of: "[") else { return nil }
        var depth = 0
        var inString = false
        var escaped = false
        var index = start
        while index < response.endIndex {
            let character = response[index]
            if inString {
                if escaped {
                    escaped = false
                } else if character == "\\" {
                    escaped = true
                } else if character == "\"" {
                    inString = false
                }
            } else {
                switch character {
                case "\"":
                    inString = true
                case "[":
                    depth += 1
                case "]":
                    depth -= 1
                    if depth == 0 {
                        return String(response[start...index])
                    }
                default:
                    break
                }
            }
            response.formIndex(after: &index)
        }
        return nil
    }

    private struct RawSuggestion: Decodable {
        let spoken: String?
        let written: String?
    }
}
