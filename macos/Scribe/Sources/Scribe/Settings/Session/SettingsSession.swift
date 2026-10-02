import Foundation

struct SettingsSessionAccess {
    var validate: @MainActor (SettingsDocument) -> Bool = SettingsSessionValidation.isValid
    var prepare: @MainActor (SettingsSubmission) async throws -> SettingsSubmission = { $0 }
    var commit: @MainActor (SettingsSubmission) async throws -> SettingsCommitReceipt
    var recover: @MainActor (UUID) async throws -> SettingsCommitReceipt? = { _ in throw SettingsSaveFailure.storage }
    var apply: @MainActor (SettingsCommitReceipt) async -> SettingsApplicationOutcome
    var discardPreparation: @MainActor (UUID) async -> Void = { _ in }
}

/// One window-wide draft. Pages call edit; only save reaches the access layer.
@MainActor
final class SettingsSession: ObservableObject {
    @Published private(set) var baseline: SettingsDocument
    @Published private(set) var draft: SettingsDocument
    @Published private(set) var isSaving = false
    @Published private(set) var lastSave: SettingsSaveResult?
    @Published private(set) var credentialEdits: [SettingsCredentialID: SettingsCredentialEdit] = [:]

    private let access: SettingsSessionAccess
    private var revision: UInt64 = 0
    private var intents: [SettingsExternalSetting: SettingsExternalIntent] = [:]
    private var outsideRevisions: [SettingsExternalSetting: UInt64] = [:]
    private var waiting: [SettingsExternalSetting: SettingsExternalIntent] = [:]
    private var nextTemporaryID: Int64 = -1
    private var uncertainSubmission: SettingsSubmission?

    init(initial: SettingsDocument, access: SettingsSessionAccess) {
        baseline = initial
        draft = initial
        self.access = access
    }

    var dirtyPages: [SettingsSessionPage] {
        var draft = self.draft.withoutPlaceholders
        let baseline = self.baseline.withoutPlaceholders
        for (setting, external) in waiting {
            for key in Self.keys(for: setting) {
                draft.preferences[key] = external.values[key]
            }
        }
        var pages = Set<SettingsSessionPage>()
        for field in SettingsMigrationLedger.draftDefaults {
            let before = baseline.preferences[field.key] ?? field.missingValue
            let after = draft.preferences[field.key] ?? field.missingValue
            if after != before, let page = field.page {
                pages.insert(page)
            }
        }
        if draft.historyRetentionValue != baseline.historyRetentionValue { pages.insert(.history) }
        if draft.dictionary != baseline.dictionary || draft.wordPackSignature != baseline.wordPackSignature {
            pages.insert(.dictionary)
        }
        if draft.snippets != baseline.snippets { pages.insert(.voiceSnippets) }
        if draft.profiles != baseline.profiles { pages.insert(.appProfiles) }
        if credentialEdits.values.contains(where: { $0 != .keep }) { pages.insert(.aiCleanup) }
        return SettingsSessionPage.allCases.filter { pages.contains($0) }
    }

    var footerText: String {
        let pages = dirtyPages
        return pages.isEmpty
            ? "All changes saved"
            : "Unsaved changes: " + pages.map(\.title).joined(separator: ", ")
    }

    var hasUnsavedChanges: Bool { !dirtyPages.isEmpty }
    var hasUnresolvedCommit: Bool { uncertainSubmission != nil }

    func edit(_ change: (inout SettingsDocument) -> Void) {
        let before = draft
        change(&draft)
        guard before != draft else { return }
        revision += 1
        for setting in SettingsExternalSetting.allCases {
            let keys = Self.keys(for: setting)
            guard keys.contains(where: { before.preferences[$0] != draft.preferences[$0] }) else { continue }
            let stamp = SettingsIntentRevision.next()
            intents[setting] = SettingsExternalIntent(
                revision: stamp, values: Self.values(keys, from: draft.preferences))
            waiting[setting] = nil
        }
    }

    func temporaryRowID() -> Int64 {
        defer { nextTemporaryID -= 1 }
        return nextTemporaryID
    }

    func editCredential(_ id: SettingsCredentialID, _ edit: SettingsCredentialEdit) {
        if edit == .keep {
            credentialEdits[id] = nil
        } else {
            credentialEdits[id] = edit
        }
        revision += 1
    }

    /// Loading is not editing. A newer read must never overwrite a collection already shown or edited.
    func adoptLoadedRows(from loaded: SettingsDocument) {
        if draft.dictionary == nil && baseline.dictionary == nil {
            baseline.dictionary = loaded.dictionary
            draft.dictionary = loaded.dictionary
        }
        if draft.snippets == nil && baseline.snippets == nil {
            baseline.snippets = loaded.snippets
            draft.snippets = loaded.snippets
        }
        if draft.profiles == nil && baseline.profiles == nil {
            baseline.profiles = loaded.profiles
            draft.profiles = loaded.profiles
        }
    }

    /// The request and its later completion use the same revision; completion order cannot win over user intent.
    func adoptExternal(
        _ setting: SettingsExternalSetting,
        values: [String: SettingsValue],
        revision stamp: UInt64,
        canShowNow: Bool = true
    ) {
        guard stamp >= (outsideRevisions[setting] ?? 0) else { return }
        outsideRevisions[setting] = stamp
        let external = SettingsExternalIntent(revision: stamp, values: values)
        for key in Self.keys(for: setting) {
            baseline.preferences[key] = values[key]
        }
        guard stamp >= (intents[setting]?.revision ?? 0) else { return }
        intents[setting] = external
        if canShowNow {
            showExternal(setting, external)
        } else {
            waiting[setting] = external
        }
    }

    func releaseExternalChanges() {
        for (setting, external) in waiting {
            showExternal(setting, external)
        }
        waiting.removeAll()
        credentialEdits.removeAll()
    }

    func cancel() -> Bool {
        guard !isSaving, uncertainSubmission == nil else { return false }
        draft = baseline
        intents.removeAll()
        waiting.removeAll()
        revision += 1
        return true
    }

    func closeDecision(_ trigger: SettingsCloseTrigger) -> SettingsCloseDecision {
        SettingsCloseGuard.decide(
            dirty: hasUnsavedChanges, saving: isSaving || hasUnresolvedCommit, trigger: trigger)
    }

    func resolveClose(_ choice: SettingsCloseChoice) async -> Bool {
        switch choice {
        case .keepEditing: return false
        case .discard: return cancel()
        case .save: return await save().mayClose
        }
    }

    func saveAndClose() async -> Bool {
        await save().mayClose
    }

    func save() async -> SettingsSaveResult {
        guard !isSaving else { return .notCommitted(.busy) }
        if let submission = uncertainSubmission {
            isSaving = true
            defer { isSaving = false }
            do {
                guard let receipt = try await access.recover(submission.id) else {
                    uncertainSubmission = nil
                    await access.discardPreparation(submission.id)
                    let result = SettingsSaveResult.notCommitted(.storage)
                    lastSave = result
                    return result
                }
                uncertainSubmission = nil
                return await acknowledge(receipt, submission: submission)
            } catch {
                return .outcomeUnknown(submission.id)
            }
        }
        if !hasUnsavedChanges && intents.isEmpty {
            if case .committed(let receipt, .notApplied, _)? = lastSave {
                let applied = await access.apply(receipt)
                let result = SettingsSaveResult.committed(
                    receipt, applied, changedWhileSaving: hasUnsavedChanges)
                lastSave = result
                return result
            }
            return .unchanged
        }
        var document = draft.withoutPlaceholders
        guard access.validate(document) else { return .notCommitted(.validation) }
        for (setting, external) in waiting {
            for key in Self.keys(for: setting) {
                document.preferences[key] = external.values[key]
            }
        }
        var submission = SettingsSubmission(
            id: UUID(), revision: revision, baseline: baseline, document: document, intents: intents)
        submission.credentials = credentialEdits
        isSaving = true
        defer { isSaving = false }
        let receipt: SettingsCommitReceipt
        do {
            let captured = submission
            let prepared = try await access.prepare(submission)
            guard prepared.id == captured.id, prepared.revision == captured.revision,
                prepared.baseline == captured.baseline, prepared.intents == captured.intents
            else {
                throw SettingsSaveFailure.validation
            }
            submission = prepared
            try Task.checkCancellation()
            receipt = try await access.commit(submission)
        } catch {
            if let uncertain = error as? SettingsCommitUncertain {
                uncertainSubmission = submission
                let result = SettingsSaveResult.outcomeUnknown(uncertain.id)
                lastSave = result
                return result
            }
            await access.discardPreparation(submission.id)
            let failure = (error as? SettingsSaveFailure) ?? (error is CancellationError ? .cancelled : .storage)
            let result = SettingsSaveResult.notCommitted(failure)
            lastSave = result
            return result
        }
        return await acknowledge(receipt, submission: submission)
    }

    private func acknowledge(
        _ receipt: SettingsCommitReceipt, submission: SettingsSubmission
    ) async -> SettingsSaveResult {
        let changed = revision != submission.revision
        baseline = receipt.document
        if !changed {
            draft = receipt.document
        } else {
            // Rebase values untouched since submission without discarding newer edits.
            for field in SettingsMigrationLedger.draftDefaults {
                if draft.preferences[field.key] == submission.document.preferences[field.key] {
                    draft.preferences[field.key] = receipt.document.preferences[field.key]
                }
            }
            if draft.dictionary == submission.document.dictionary {
                draft.dictionary = receipt.document.dictionary
            } else if let rows = draft.dictionary {
                draft.dictionary = rows.map {
                    var row = $0
                    row.id = receipt.rowIDs["dictionary:\(row.id)"] ?? row.id
                    return row
                }
            }
            if draft.snippets == submission.document.snippets {
                draft.snippets = receipt.document.snippets
            } else if let rows = draft.snippets {
                draft.snippets = rows.map {
                    var row = $0
                    row.id = receipt.rowIDs["snippets:\(row.id)"] ?? row.id
                    return row
                }
            }
            if draft.profiles == submission.document.profiles {
                draft.profiles = receipt.document.profiles
            } else if let rows = draft.profiles {
                draft.profiles = rows.map {
                    var row = $0
                    row.id = receipt.rowIDs["profiles:\(row.id)"] ?? row.id
                    return row
                }
            }
            if draft.historyRetentionValue == submission.document.historyRetentionValue {
                draft.historyRetentionValue = receipt.document.historyRetentionValue
            }
        }
        for (setting, intent) in submission.intents {
            if (intents[setting]?.revision ?? 0) <= intent.revision {
                intents[setting] = nil
                waiting[setting] = nil
            }
        }
        for (id, edit) in submission.credentials where credentialEdits[id] == edit {
            credentialEdits[id] = nil
        }
        let applied = await access.apply(receipt)
        let result = SettingsSaveResult.committed(
            receipt, applied, changedWhileSaving: changed || hasUnsavedChanges)
        lastSave = result
        return result
    }

    private func showExternal(_ setting: SettingsExternalSetting, _ external: SettingsExternalIntent) {
        for key in Self.keys(for: setting) {
            draft.preferences[key] = external.values[key]
        }
        waiting[setting] = nil
        revision += 1
    }

    nonisolated static func keys(for setting: SettingsExternalSetting) -> [String] {
        switch setting {
        case .aiCleanup: return ["ScribeAiCleanupEnabled"]
        case .microphone: return ["ScribeInputDeviceUID", "ScribeInputDeviceName"]
        case .overlayAnchor: return ["ScribeOverlayAnchor"]
        }
    }

    private static func values(_ keys: [String], from preferences: SettingsPreferences) -> [String: SettingsValue] {
        var result: [String: SettingsValue] = [:]
        for key in keys {
            result[key] = preferences[key]
        }
        return result
    }
}
