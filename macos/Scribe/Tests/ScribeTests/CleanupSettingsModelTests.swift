import XCTest
@testable import Scribe

/// In-memory stand-in for `CleanupSettingsStore`, Keychain and the provider. A save posts
/// `UserDefaults.didChangeNotification` on the model's center, as a real `UserDefaults` write does, so the tests
/// cover the model's guard against its own writes as well as its reaction to writes from elsewhere.
@MainActor
final class CleanupSettingsBackingFake {
    let center = NotificationCenter()
    /// Stands in for the app-owned drafts, which outlive any one tab.
    let drafts = SettingsDrafts()
    var stored = CleanupSettingsValues(
        isEnabled: false,
        providerKind: .foundryLocal,
        foundryLocalModelAlias: "qwen2.5-1.5b",
        ollamaModel: "qwen2.5:3b",
        openAIBaseURL: "",
        openAIModel: "",
        azureEndpoint: "",
        azureDeployment: "",
        azureAuthMode: .azureCli,
        azureTenantId: "",
        azureClientId: "")
    private(set) var saves: [CleanupSettingsValues] = []
    var apiKey: String?
    var clientSecrets: [String: String] = [:]
    var configured: Set<CleanupProviderKind> = [.foundryLocal, .ollama]
    var connectionCheck = CleanupConnectionCheck(reachable: true, message: "Foundry Local: ready")
    var checkGate: SettingsTestGate?
    /// Makes the next Keychain write or removal throw, as a locked or unavailable Keychain would.
    var failNextKeychainWrite = false

    /// A write made outside the tab, such as the tray's AI Cleanup item.
    func storeFromElsewhere(_ change: (inout CleanupSettingsValues) -> Void) {
        change(&stored)
        center.post(name: UserDefaults.didChangeNotification, object: nil)
    }

    /// Captures `connectionCheck` and `checkGate` as they are now, so set those first.
    var access: CleanupSettingsAccess {
        let check = connectionCheck
        let gate = checkGate
        return CleanupSettingsAccess(
            load: { self.stored },
            save: { new, _ in
                self.stored = new
                self.saves.append(new)
                self.center.post(name: UserDefaults.didChangeNotification, object: nil)
            },
            isConfigured: { self.configured.contains($0) },
            hasOpenAIApiKey: { self.apiKey != nil },
            setOpenAIApiKey: { key in
                try self.failIfAsked()
                self.apiKey = (key?.isEmpty == false) ? key : nil
            },
            hasAzureClientSecret: { self.clientSecrets[$0] != nil },
            setAzureClientSecret: { secret, clientId in
                try self.failIfAsked()
                self.clientSecrets[clientId] = secret
            },
            checkConnection: {
                if let gate {
                    await gate.pass()
                }
                return check
            })
    }

    private func failIfAsked() throws {
        if failNextKeychainWrite {
            failNextKeychainWrite = false
            throw SettingsKeychainWriteFailure()
        }
    }
}

struct SettingsKeychainWriteFailure: Error {}

final class CleanupSettingsModelTests: XCTestCase {
    @MainActor
    private func makeModel(_ backing: CleanupSettingsBackingFake) -> CleanupSettingsModel {
        CleanupSettingsModel(access: backing.access, drafts: backing.drafts, center: backing.center)
    }

    /// Pins the fix for an outer `.disabled(!isEnabled)` that also disabled the Enable switch itself, so cleanup
    /// could only be turned on from the tray.
    @MainActor
    func testTheEnableSwitchStaysUsableWhileCleanupIsOff() {
        let backing = CleanupSettingsBackingFake()
        let model = makeModel(backing)

        XCTAssertFalse(model.values.isEnabled)
        XCTAssertFalse(model.isDisabled(.enableSwitch))
        XCTAssertTrue(model.isDisabled(.provider))
        XCTAssertTrue(model.isDisabled(.providerDetails))
        XCTAssertTrue(model.isDisabled(.connectionTest))

        model.values.isEnabled = true

        XCTAssertTrue(backing.stored.isEnabled)
        XCTAssertFalse(model.isDisabled(.enableSwitch))
        XCTAssertFalse(model.isDisabled(.provider))
        XCTAssertFalse(model.isDisabled(.providerDetails))
        XCTAssertFalse(model.isDisabled(.connectionTest))
    }

    @MainActor
    func testATrayChangeIsShownWhileTheTabIsOpen() {
        let backing = CleanupSettingsBackingFake()
        let model = makeModel(backing)

        backing.storeFromElsewhere { $0.isEnabled = true }
        XCTAssertTrue(model.values.isEnabled)

        backing.storeFromElsewhere { $0.isEnabled = false }
        XCTAssertFalse(model.values.isEnabled)
        XCTAssertTrue(model.isDisabled(.provider))
    }

    /// Windows 0.4.3 parity: the newest choice for the AI switch wins, whether it was made in the tray or in
    /// Settings, and an edit in the tab never writes back a value the tab did not show.
    @MainActor
    func testATrayChoiceSurvivesALaterEditInTheTab() {
        let backing = CleanupSettingsBackingFake()
        let model = makeModel(backing)

        backing.storeFromElsewhere { $0.isEnabled = true }
        XCTAssertEqual(backing.saves.count, 0, "re-reading what is stored must not write it back")

        model.values.openAIBaseURL = "http://localhost:1234"

        XCTAssertEqual(backing.saves.count, 1)
        XCTAssertEqual(backing.stored.openAIBaseURL, "http://localhost:1234")
        XCTAssertTrue(backing.stored.isEnabled)
    }

    /// The re-read on appear goes through the same path as a tray change, and must not store back what it read.
    @MainActor
    func testReReadingOnAppearShowsStoredValuesWithoutWritingThemBack() {
        let backing = CleanupSettingsBackingFake()
        let model = makeModel(backing)
        backing.stored.isEnabled = true
        backing.stored.providerKind = .ollama

        model.reload()

        XCTAssertTrue(model.values.isEnabled)
        XCTAssertEqual(model.values.providerKind, .ollama)
        XCTAssertEqual(backing.saves.count, 0)
    }

    @MainActor
    func testWhatIsTypedIntoASecretFieldSurvivesAReload() {
        let backing = CleanupSettingsBackingFake()
        let model = makeModel(backing)
        backing.drafts.openAIApiKey = "typed but not saved"

        // The model has to be alive, and observing, when the change arrives from elsewhere.
        withExtendedLifetime(model) {
            backing.storeFromElsewhere { $0.isEnabled = true }
        }

        XCTAssertEqual(backing.drafts.openAIApiKey, "typed but not saved")
        XCTAssertNil(backing.apiKey)
    }

    @MainActor
    func testSavingAndClearingTheAPIKeyUpdatesWhatTheTabShows() {
        let backing = CleanupSettingsBackingFake()
        let model = makeModel(backing)
        model.refreshSecretState()
        XCTAssertFalse(model.hasSavedOpenAIApiKey)
        XCTAssertFalse(model.canSaveOpenAIApiKey)

        backing.drafts.openAIApiKey = "key"
        model.saveOpenAIApiKey()

        XCTAssertEqual(backing.apiKey, "key")
        XCTAssertTrue(model.hasSavedOpenAIApiKey)
        XCTAssertEqual(backing.drafts.openAIApiKey, "")

        model.clearOpenAIApiKey()

        XCTAssertNil(backing.apiKey)
        XCTAssertFalse(model.hasSavedOpenAIApiKey)
    }

    @MainActor
    func testTheClientSecretStateFollowsTheClientID() {
        let backing = CleanupSettingsBackingFake()
        backing.clientSecrets["app-a"] = "secret"
        let model = makeModel(backing)

        model.values.azureClientId = "app-a"
        XCTAssertTrue(model.hasSavedAzureClientSecret)

        model.values.azureClientId = "app-b"
        XCTAssertFalse(model.hasSavedAzureClientSecret)
        XCTAssertFalse(model.canSaveAzureClientSecret)

        backing.drafts.azureClientSecret = "another"
        XCTAssertTrue(model.canSaveAzureClientSecret)
    }

    @MainActor
    func testConnectionTestShowsTheProvidersAnswer() async {
        let backing = CleanupSettingsBackingFake()
        backing.stored.isEnabled = true
        backing.connectionCheck = CleanupConnectionCheck(reachable: false, message: "Ollama: not running")
        let model = makeModel(backing)

        await model.testConnection()

        XCTAssertEqual(model.errorMessage, "Ollama: not running")
        XCTAssertNil(model.statusMessage)
        XCTAssertFalse(model.isTesting)
    }

    @MainActor
    func testAConnectionResultForSettingsChangedMeanwhileIsDropped() async {
        let backing = CleanupSettingsBackingFake()
        backing.stored.isEnabled = true
        let gate = SettingsTestGate()
        backing.checkGate = gate
        let model = makeModel(backing)

        let test = Task { await model.testConnection() }
        await gate.waitForArrival()
        XCTAssertTrue(model.isTesting)
        model.values.foundryLocalModelAlias = "phi-3.5-mini"
        await gate.open()
        await test.value

        XCTAssertNil(model.statusMessage)
        XCTAssertNil(model.errorMessage)
        XCTAssertFalse(model.isTesting)
    }

    @MainActor
    func testConnectionTestNeedsAConfiguredProvider() {
        let backing = CleanupSettingsBackingFake()
        backing.stored.isEnabled = true
        backing.stored.providerKind = .openAICompatible
        let model = makeModel(backing)

        XCTAssertTrue(model.isDisabled(.connectionTest))

        backing.configured.insert(.openAICompatible)

        XCTAssertFalse(model.isDisabled(.connectionTest))
    }

    /// Settings is rebuilt on every open; a key typed but not saved lives in the drafts, which outlive the tab.
    @MainActor
    func testATypedKeySurvivesTheTabBeingBuiltAgain() {
        let backing = CleanupSettingsBackingFake()
        do {
            let closedTab = makeModel(backing)
            backing.drafts.openAIApiKey = "typed before the window closed"
            withExtendedLifetime(closedTab) {}
        }

        let rebuilt = makeModel(backing)
        XCTAssertTrue(rebuilt.canSaveOpenAIApiKey)
        rebuilt.saveOpenAIApiKey()

        XCTAssertEqual(backing.apiKey, "typed before the window closed")
        XCTAssertEqual(backing.drafts.openAIApiKey, "")
    }

    // Clear means no credential: a replacement typed but not saved goes with it, so it cannot come back when
    // Settings reopens and offer Save for a key the user just removed.

    @MainActor
    func testClearingTheKeyAlsoDropsATypedReplacement() {
        let backing = openAICompatibleBacking()
        backing.apiKey = "saved"
        do {
            let tab = makeModel(backing)
            tab.refreshSecretState()
            backing.drafts.openAIApiKey = "typed replacement"

            tab.clearOpenAIApiKey()

            XCTAssertEqual(tab.statusMessage, "API key removed.")
        }

        let reopened = makeModel(backing)
        reopened.refreshSecretState()
        XCTAssertNil(backing.apiKey)
        XCTAssertEqual(backing.drafts.openAIApiKey, "")
        XCTAssertFalse(reopened.canSaveOpenAIApiKey)
        XCTAssertFalse(reopened.hasSavedOpenAIApiKey)
    }

    @MainActor
    func testClearingTheClientSecretAlsoDropsATypedReplacement() {
        let backing = servicePrincipalBacking()
        backing.clientSecrets["app-a"] = "saved"
        do {
            let tab = makeModel(backing)
            tab.refreshSecretState()
            backing.drafts.azureClientSecret = "typed replacement"

            tab.clearAzureClientSecret()

            XCTAssertEqual(tab.statusMessage, "Client secret removed.")
        }

        let reopened = makeModel(backing)
        reopened.refreshSecretState()
        XCTAssertNil(backing.clientSecrets["app-a"])
        XCTAssertEqual(backing.drafts.azureClientSecret, "")
        XCTAssertFalse(reopened.canSaveAzureClientSecret)
        XCTAssertFalse(reopened.hasSavedAzureClientSecret)
    }

    @MainActor
    func testAFailedClearKeepsTheSavedKeyAndWhatWasTyped() {
        let backing = openAICompatibleBacking()
        backing.apiKey = "saved"
        let model = makeModel(backing)
        model.refreshSecretState()
        backing.drafts.openAIApiKey = "typed replacement"
        backing.failNextKeychainWrite = true

        model.clearOpenAIApiKey()

        XCTAssertEqual(backing.apiKey, "saved")
        XCTAssertEqual(backing.drafts.openAIApiKey, "typed replacement")
        XCTAssertTrue(model.hasSavedOpenAIApiKey)
        XCTAssertNotNil(model.errorMessage)
    }

    // A connection check that was running when a stored credential changed checked the credential that was
    // replaced; its result must not appear against the new one or overwrite the message about the change.

    @MainActor
    func testClearingTheKeyDuringAConnectionTestDropsTheOldResult() async {
        let backing = openAICompatibleBacking()
        backing.apiKey = "old"

        let model = await runConnectionTest(backing) { $0.clearOpenAIApiKey() }

        XCTAssertNil(backing.apiKey)
        XCTAssertEqual(model.statusMessage, "API key removed.")
        XCTAssertNil(model.errorMessage)
        XCTAssertFalse(model.isTesting)
    }

    @MainActor
    func testReplacingTheKeyDuringAConnectionTestDropsTheOldResult() async {
        let backing = openAICompatibleBacking()
        backing.apiKey = "old"

        let model = await runConnectionTest(backing) { model in
            backing.drafts.openAIApiKey = "new"
            model.saveOpenAIApiKey()
        }

        XCTAssertEqual(backing.apiKey, "new")
        XCTAssertEqual(model.statusMessage, "API key saved to Keychain.")
        XCTAssertNil(model.errorMessage)
    }

    @MainActor
    func testClearingTheClientSecretDuringAConnectionTestDropsTheOldResult() async {
        let backing = servicePrincipalBacking()
        backing.clientSecrets["app-a"] = "old"

        let model = await runConnectionTest(backing) { $0.clearAzureClientSecret() }

        XCTAssertNil(backing.clientSecrets["app-a"])
        XCTAssertEqual(model.statusMessage, "Client secret removed.")
        XCTAssertNil(model.errorMessage)
    }

    @MainActor
    func testReplacingTheClientSecretDuringAConnectionTestDropsTheOldResult() async {
        let backing = servicePrincipalBacking()
        backing.clientSecrets["app-a"] = "old"

        let model = await runConnectionTest(backing) { model in
            backing.drafts.azureClientSecret = "new"
            model.saveAzureClientSecret()
        }

        XCTAssertEqual(backing.clientSecrets["app-a"], "new")
        XCTAssertEqual(model.statusMessage, "Client secret saved to Keychain.")
        XCTAssertNil(model.errorMessage)
    }

    @MainActor
    private func openAICompatibleBacking() -> CleanupSettingsBackingFake {
        let backing = CleanupSettingsBackingFake()
        backing.stored.isEnabled = true
        backing.stored.providerKind = .openAICompatible
        backing.stored.openAIBaseURL = "http://localhost:1234"
        backing.stored.openAIModel = "local-model"
        backing.configured.insert(.openAICompatible)
        return backing
    }

    @MainActor
    private func servicePrincipalBacking() -> CleanupSettingsBackingFake {
        let backing = CleanupSettingsBackingFake()
        backing.stored.isEnabled = true
        backing.stored.providerKind = .microsoftFoundry
        backing.stored.azureAuthMode = .servicePrincipal
        backing.stored.azureClientId = "app-a"
        backing.configured.insert(.microsoftFoundry)
        return backing
    }

    /// Holds a successful connection check at a gate, applies `change` while it is held, then lets it finish.
    @MainActor
    private func runConnectionTest(
        _ backing: CleanupSettingsBackingFake,
        whileRunning change: (CleanupSettingsModel) -> Void
    ) async -> CleanupSettingsModel {
        let gate = SettingsTestGate()
        backing.checkGate = gate
        backing.connectionCheck = CleanupConnectionCheck(reachable: true, message: "Reachable with the old credential")
        let model = makeModel(backing)
        model.refreshSecretState()

        let test = Task { await model.testConnection() }
        await gate.waitForArrival()
        XCTAssertTrue(model.isTesting)
        change(model)
        await gate.open()
        await test.value
        return model
    }
}
