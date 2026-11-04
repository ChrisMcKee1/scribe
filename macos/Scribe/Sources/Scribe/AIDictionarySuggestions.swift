import Combine
import Foundation

struct AIDictionarySuggestionAccess: Sendable {
    var loadEntries: @Sendable () async throws -> [DictionaryEntry]
    /// The newest `limit` dictations as they were typed, newest first, within the chosen retention.
    var loadHistory: @Sendable (_ limit: Int) async throws -> [String]
    /// Adds the reviewed suggestions and repairs the rules they correct, in one transaction.
    var addOrRepair: @Sendable ([DictionaryEntry]) async throws -> DictionarySuggestionCommit

    static func live(_ store: PersistenceStore) -> AIDictionarySuggestionAccess {
        AIDictionarySuggestionAccess(
            loadEntries: { try await store.loadAllDictionaryEntries() },
            loadHistory: { try await store.loadRecentTranscripts(limit: $0) },
            addOrRepair: { try await store.addOrRepairDictionaryEntries($0) })
    }
}

struct AIDictionarySuggestionAdmission: Sendable {
    let recipient: CleanupConnection
    let isCurrent: @MainActor @Sendable () -> Bool
    let complete: @Sendable (_ systemPrompt: String, _ userMessage: String) async throws -> String
}

struct AIDictionarySuggestionService {
    let admit: @MainActor @Sendable () -> AIDictionarySuggestionAdmission?

    @MainActor static var live: Self { backed(by: .shared, operations: .shared) }

    static func backed(by cache: CleanupProviderCache, operations: AuxiliaryOperations) -> Self {
        Self(admit: {
            guard let admission = try? cache.admitOneOff() else { return nil }
            return AIDictionarySuggestionAdmission(
                recipient: admission.connection,
                isCurrent: { cache.isCurrent(admission) },
                complete: { prompt, sample in
                    try await operations.run {
                        let response = try await cache.completeOneOff(
                            CleanupRequest(
                                transcript: sample, writingStylePrompt: prompt, maxOutputTokens: 2048),
                            admission: admission)
                        return response.cleanedText
                    }
                })
        })
    }
}

struct AIDictionarySuggestionConsent: Identifiable {
    let id: UUID
    let title: String
    let message: String

    fileprivate let admission: AIDictionarySuggestionAdmission
    fileprivate let attempt: UInt64
    fileprivate let sample: String
}

/// Learn from history (Settings > Dictionary > Your words), as on Windows. While AI cleanup serves a configuration,
/// the most recent dictations from saved history (`AIDictionarySuggester.buildHistorySample`) go to that AI service,
/// after asking unless it is Foundry Local (`CleanupDisclosure.suggestionConsentIsNeeded`), and the reviewed
/// suggestions are added or repair the rule with the same spoken form. Otherwise, or when the AI service finds nothing
/// usable, the history scan (`DictionaryHistoryLearner`) runs instead.
///
/// The request carries the suggestion instructions and the sample only: no dictionary, writing style or audio. The
/// sample is history as it was typed, so it can hold words the dictionary and snippets added, which the question says.
/// The admission is captured before the question and checked again before the request, after the answer and on every
/// settings change, and the one-off handoff checks it on every send, so the dictations go where the question said or
/// nowhere, and an answer from a configuration that changed meanwhile is discarded.
@MainActor
final class AIDictionarySuggestionModel: ObservableObject {
    /// The instructions sent with the history sample, word for word as Windows sends them
    /// (`AiDictionarySuggester.SystemPrompt`).
    static let systemPrompt =
        "You help build a personal dictation dictionary. You are given a sample of text that a "
        + "speech-to-text system produced from one user's recent dictation. Read the surrounding sentence, "
        + "topic, and nearby dictations carefully: just because a phrase was transcribed or rewritten into "
        + "valid words does not mean it was written as the right term. Find the technical terms, product "
        + "names, brand names, acronyms, commands, and jargon in the sample that speech-to-text or an "
        + "earlier rewrite got wrong in context, or is likely to spell wrong, mis-case, or mishear.\n\n"
        + "For each such term (a short word or phrase, never a full sentence), output two fields:\n"
        + "- \"written\": the correct way to write the term in context (correct spelling, casing, punctuation, "
        + "hyphenation).\n"
        + "- \"spoken\": how that term came out in the sample when misheard or mis-rewritten, or how it most "
        + "likely comes out of a speech-to-text system when the user says it out loud, written in plain "
        + "lowercase the way a recognizer transcribes the sound. For an acronym said letter by letter, use "
        + "single spaced letters (for example \"a p i m\" for APIM). For a term said as a word, use its "
        + "phonetic lowercase spelling (for example \"cube control\" for kubectl, \"post gres\" for "
        + "PostgreSQL). For a term that is only mis-cased, use the lowercase of the written form.\n\n"
        + "Rules:\n"
        + "- Only include terms that actually appear in the sample (either as the spoken mishearing or the written "
        + "term).\n"
        + "- Prefer high-value, unambiguous terms backed by the surrounding context. Skip ordinary English "
        + "words, full sentences, and anything you are unsure about.\n"
        + "- If existing vocabulary rules are listed below, use them as supporting evidence for the user's "
        + "domain to infer what a failed or mis-rewritten dictation in the sample was meant to say. Do not "
        + "assume every existing rule is accurate: if an existing rule maps a spoken form to the wrong written "
        + "term or casing in context, include that spoken form with the corrected written form so the "
        + "dictionary can self-heal; skip only spoken forms whose existing written replacement is already right.\n"
        + "- \"spoken\" must be lowercase, and \"spoken\" must not be identical to \"written\".\n"
        + "- Return at most 25 items, fewer is fine.\n"
        + "- Output ONLY a JSON array of objects, each with exactly the keys \"spoken\" and \"written\". "
        + "No commentary, no code fences, no other text.\n\n"
        + "Example output:\n"
        + "[{\"spoken\":\"a p i m\",\"written\":\"APIM\"},{\"spoken\":\"cosmos db\",\"written\":\"Cosmos DB\"},"
        + "{\"spoken\":\"post gres\",\"written\":\"PostgreSQL\"}]"

    static let nothingToLearnFrom =
        "There are no recent dictations to learn from yet. Keep dictating and try again later."
    static let historyUnreadable = "Couldn't read your dictation history. Nothing was sent."
    static let dictionaryUnreadable = "Couldn't read your dictionary, so no suggestion was added."
    static let changedBeforeSending =
        "Where AI cleanup runs changed before the request went out, so Scribe sent nothing. Choose Learn from "
        + "history again to decide for the AI service in use now."
    static let changedAfterSending =
        "Where AI cleanup runs changed after the request went out, so Scribe discarded the suggestions and added "
        + "nothing. Choose Learn from history again."
    static let requestFailed = "Couldn't get AI suggestions. Nothing was added."
    static let commitFailed = "Couldn't add the reviewed suggestions to your dictionary."

    /// Runs the history scan; `afterAI` says the AI service ran first and found nothing usable.
    typealias HistoryScan = @MainActor @Sendable (_ afterAI: Bool) async -> Void

    @Published private(set) var consent: AIDictionarySuggestionConsent?
    @Published private(set) var suggestions: [DictionaryEntry] = []
    @Published private(set) var selectedSpokenForms: Set<String> = []
    @Published private(set) var isPreparing = false
    @Published private(set) var isRequesting = false
    @Published private(set) var isCommitting = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var statusMessage: String?
    @Published private(set) var isReviewPresented = false
    /// Moves after every change this model writes to the dictionary, so the Your words list reads its rows again.
    @Published private(set) var dictionaryRevision: UInt64 = 0
    /// Reading history and asking the AI service.
    private(set) var inFlight: Task<Void, Never>?
    /// The history scan, which leaving the tab does not stop, as it never did.
    private(set) var scanTask: Task<Void, Never>?

    private let access: AIDictionarySuggestionAccess
    private let service: AIDictionarySuggestionService?
    private let onChanged: @MainActor @Sendable () -> Void
    private var requestID: UInt64 = 0
    private var requestRecipient: AIDictionarySuggestionAdmission?
    private var reviewRecipient: AIDictionarySuggestionAdmission?
    private var historyScan: HistoryScan?
    /// What each suggested spoken form is written as now, for the suggestions that repair a rule.
    private var currentReplacements: [String: String] = [:]
    private var observation: SettingsNotificationObservation?

    init(
        access: AIDictionarySuggestionAccess,
        service: AIDictionarySuggestionService? = nil,
        center: NotificationCenter = .default,
        onChanged: @escaping @MainActor @Sendable () -> Void = {}
    ) {
        self.access = access
        self.service = service
        self.onChanged = onChanged
        observation = SettingsNotificationObservation(UserDefaults.didChangeNotification, center: center) {
            [weak self] in
            self?.invalidateIfRecipientChanged()
        }
    }

    var isBusy: Bool {
        isPreparing || isRequesting || isCommitting || consent != nil
    }

    /// Whether Learn from history would ask the AI service now, rather than scan history on this Mac.
    var usesAIService: Bool {
        service?.admit() != nil
    }

    var canCommitSelection: Bool {
        !isCommitting && !selectedSpokenForms.isEmpty
    }

    /// Learn from history. With AI cleanup serving a configuration, reads the most recent dictations and asks before
    /// sending them unless that configuration is Foundry Local; without one, runs `scanHistory` at once.
    func learnFromHistory(scanHistory: @escaping HistoryScan) {
        guard !isBusy else { return }
        errorMessage = nil
        statusMessage = nil
        historyScan = scanHistory
        guard let service, let admission = service.admit() else {
            scanTask = Task { await scanHistory(false) }
            return
        }

        requestID &+= 1
        let attempt = requestID
        requestRecipient = admission
        isPreparing = true
        let access = self.access
        inFlight = Task { [weak self] in
            let history: [String]
            do {
                history = try await access.loadHistory(AIDictionarySuggester.historyLimit)
                try Task.checkCancellation()
            } catch is CancellationError {
                self?.finish(attempt)
                return
            } catch {
                guard let self, self.requestID == attempt else { return }
                self.finish(attempt)
                self.errorMessage = Self.historyUnreadable
                return
            }
            guard let self, self.stillServes(attempt, admission, otherwise: Self.changedBeforeSending) else {
                return
            }

            let sample = AIDictionarySuggester.buildHistorySample(history)
            guard !sample.isEmpty else {
                self.finish(attempt)
                self.statusMessage = Self.nothingToLearnFrom
                return
            }
            guard !CleanupDisclosure.suggestionConsentIsNeeded(for: admission.recipient.target) else {
                self.finish(attempt)
                self.consent = AIDictionarySuggestionConsent(
                    id: UUID(),
                    title: CleanupDisclosure.suggestionConsentTitle,
                    message: CleanupDisclosure.suggestionConsent(for: admission.recipient.target),
                    admission: admission,
                    attempt: attempt,
                    sample: sample)
                return
            }
            await self.send(sample, admission: admission, attempt: attempt)
        }
    }

    /// The affirmative answer to `consent`. The consent is passed in, since the question can be dismissed, clearing
    /// `consent`, before its button's action runs.
    func requestSuggestions(_ answered: AIDictionarySuggestionConsent? = nil) {
        guard let consent = answered ?? consent else { return }
        self.consent = nil
        guard !isPreparing, !isRequesting, !isCommitting, requestID == consent.attempt,
            consent.admission.isCurrent()
        else {
            errorMessage = Self.changedBeforeSending
            return
        }
        isRequesting = true
        requestRecipient = consent.admission
        inFlight = Task { [weak self] in
            await self?.send(consent.sample, admission: consent.admission, attempt: consent.attempt)
        }
    }

    func cancelConsent() {
        consent = nil
    }

    func isSelected(_ entry: DictionaryEntry) -> Bool {
        selectedSpokenForms.contains(AIDictionarySuggester.spokenKey(entry.pattern))
    }

    func toggleSelection(for entry: DictionaryEntry, selected: Bool) {
        let key = AIDictionarySuggester.spokenKey(entry.pattern)
        if selected {
            selectedSpokenForms.insert(key)
        } else {
            selectedSpokenForms.remove(key)
        }
    }

    /// What Scribe writes now for a suggestion that repairs a rule, or nil for a new word.
    func currentReplacement(for entry: DictionaryEntry) -> String? {
        currentReplacements[AIDictionarySuggester.spokenKey(entry.pattern)]
    }

    /// Writes only the entries selected in the review sheet. The store decides, in its transaction, which are new and
    /// which repair a stored rule, so an edit or import made meanwhile cannot create duplicate rules.
    func commitSelectedSuggestions() async {
        guard canCommitSelection else { return }
        let selected = suggestions.filter { isSelected($0) }
        isCommitting = true
        defer { isCommitting = false }
        do {
            let commit = try await access.addOrRepair(selected)
            dismissReview()
            statusMessage = Self.commitSummary(commit)
            if !commit.isEmpty {
                dictionaryRevision &+= 1
                onChanged()
            }
        } catch {
            errorMessage = Self.commitFailed
        }
    }

    func dismissReview() {
        suggestions = []
        selectedSpokenForms = []
        currentReplacements = [:]
        isReviewPresented = false
        reviewRecipient = nil
    }

    /// Stops reading history and asking the AI service, and closes the question and the review. A history scan
    /// already running finishes.
    func cancel() {
        requestID &+= 1
        inFlight?.cancel()
        inFlight = nil
        requestRecipient = nil
        isPreparing = false
        isRequesting = false
        consent = nil
        dismissReview()
    }

    static func commitSummary(_ commit: DictionarySuggestionCommit) -> String {
        let added = commit.added.count
        let repaired = commit.repaired.count
        switch (added, repaired) {
        case (0, 0):
            return "Those words are already in your dictionary."
        case (_, 0):
            return "Added \(words(added)) to your dictionary."
        case (0, _):
            return "Fixed how Scribe writes \(words(repaired)) in your dictionary."
        default:
            return "Added \(words(added)) to your dictionary and fixed how Scribe writes \(words(repaired))."
        }
    }

    private static func words(_ count: Int) -> String {
        count == 1 ? "1 word" : "\(count) words"
    }

    private func send(_ sample: String, admission: AIDictionarySuggestionAdmission, attempt: UInt64) async {
        guard requestID == attempt else { return }
        isPreparing = false
        isRequesting = true
        requestRecipient = admission

        let response: String
        do {
            try Task.checkCancellation()
            response = try await admission.complete(Self.systemPrompt, sample)
            try Task.checkCancellation()
        } catch is CancellationError {
            finish(attempt)
            return
        } catch is CleanupSendHandoff.Refusal {
            guard requestID == attempt else { return }
            invalidate(Self.changedBeforeSending)
            return
        } catch {
            guard stillServes(attempt, admission, otherwise: Self.changedAfterSending) else { return }
            finish(attempt)
            errorMessage = Self.requestFailed
            return
        }
        guard stillServes(attempt, admission, otherwise: Self.changedAfterSending) else { return }

        let existing: [DictionaryEntry]
        do {
            existing = try await access.loadEntries()
            try Task.checkCancellation()
        } catch is CancellationError {
            finish(attempt)
            return
        } catch {
            guard requestID == attempt else { return }
            finish(attempt)
            errorMessage = Self.dictionaryUnreadable
            return
        }
        guard stillServes(attempt, admission, otherwise: Self.changedAfterSending) else { return }

        // Windows normalizes dashes in every one-off answer before it is read. A dash written as a JSON escape survives
        // that, so a suggestion still holding one is left out, as this parser always left them out.
        let parsed = AIDictionarySuggester.parseSuggestions(DashNormalizer.normalize(response), existing: existing)
            .filter { !DashNormalizer.containsDash($0.pattern) && !DashNormalizer.containsDash($0.replacement) }
        finish(attempt)
        guard !parsed.isEmpty else {
            if let historyScan {
                scanTask = Task { await historyScan(true) }
            }
            return
        }

        var writtenBySpoken: [String: String] = [:]
        for entry in existing {
            let key = AIDictionarySuggester.spokenKey(entry.pattern)
            if !key.isEmpty, writtenBySpoken[key] == nil {
                writtenBySpoken[key] = entry.replacement
            }
        }
        currentReplacements = [:]
        for suggestion in parsed {
            let key = AIDictionarySuggester.spokenKey(suggestion.pattern)
            if let current = writtenBySpoken[key] {
                currentReplacements[key] = current
            }
        }
        suggestions = parsed
        selectedSpokenForms = Set(parsed.map { AIDictionarySuggester.spokenKey($0.pattern) })
        reviewRecipient = admission
        isReviewPresented = true
    }

    /// True while `attempt` is the newest and its configuration still serves; otherwise discards it with `message`
    /// when the configuration changed.
    private func stillServes(
        _ attempt: UInt64, _ admission: AIDictionarySuggestionAdmission, otherwise message: String
    ) -> Bool {
        guard requestID == attempt else { return false }
        guard admission.isCurrent() else {
            invalidate(message)
            return false
        }
        return true
    }

    private func finish(_ attempt: UInt64) {
        guard requestID == attempt else { return }
        inFlight = nil
        requestRecipient = nil
        isPreparing = false
        isRequesting = false
    }

    private func invalidateIfRecipientChanged() {
        guard let expected = consent?.admission ?? requestRecipient ?? reviewRecipient, !expected.isCurrent() else {
            return
        }
        invalidate(isRequesting || isReviewPresented ? Self.changedAfterSending : Self.changedBeforeSending)
    }

    private func invalidate(_ message: String) {
        cancel()
        errorMessage = message
    }
}
