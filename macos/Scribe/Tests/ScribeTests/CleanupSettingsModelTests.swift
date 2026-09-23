import XCTest
@testable import Scribe

/// In-memory stand-in for `CleanupSettingsStore`, Keychain and the provider. A save posts
/// `UserDefaults.didChangeNotification` on the model's center, as a real `UserDefaults` write does, so the tests
/// cover the model's guard against its own writes as well as its reaction to writes from elsewhere.
@MainActor
final class CleanupSettingsBackingFake {
    let center = NotificationCenter()
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
            setOpenAIApiKey: { key in self.apiKey = (key?.isEmpty == false) ? key : nil },
            hasAzureClientSecret: { self.clientSecrets[$0] != nil },
            setAzureClientSecret: { secret, clientId in self.clientSecrets[clientId] = secret },
            checkConnection: {
                if let gate {
                    await gate.pass()
                }
                return check
            })
    }
}

final class CleanupSettingsModelTests: XCTestCase {
    @MainActor
    private func makeModel(_ backing: CleanupSettingsBackingFake) -> CleanupSettingsModel {
        CleanupSettingsModel(access: backing.access, center: backing.center)
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
        model.openAIApiKeyInput = "typed but not saved"

        backing.storeFromElsewhere { $0.isEnabled = true }

        XCTAssertEqual(model.openAIApiKeyInput, "typed but not saved")
        XCTAssertNil(backing.apiKey)
    }

    @MainActor
    func testSavingAndClearingTheAPIKeyUpdatesWhatTheTabShows() {
        let backing = CleanupSettingsBackingFake()
        let model = makeModel(backing)
        model.refreshSecretState()
        XCTAssertFalse(model.hasSavedOpenAIApiKey)
        XCTAssertFalse(model.canSaveOpenAIApiKey)

        model.openAIApiKeyInput = "key"
        model.saveOpenAIApiKey()

        XCTAssertEqual(backing.apiKey, "key")
        XCTAssertTrue(model.hasSavedOpenAIApiKey)
        XCTAssertEqual(model.openAIApiKeyInput, "")

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

        model.azureClientSecretInput = "another"
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
}
