import Combine
import Foundation

@MainActor
final class SpeechModelSetup: ObservableObject {
    struct Operations: Sendable {
        let list: @Sendable () async throws -> [FoundrySpeechModelChoice]
        let download: @Sendable (String) async throws -> [FoundrySpeechModelChoice]

        static let live = Self(
            list: {
                guard let cli = TranscriptionBackendResolver.live().foundryExecutable() else {
                    throw TranscriptionError.backendMissing(.foundryCliNotFound)
                }
                return try await AuxiliaryOperations.shared.run {
                    try await FoundrySpeechModelCatalog.list(cliURL: cli)
                }
            },
            download: { alias in
                guard let cli = TranscriptionBackendResolver.live().foundryExecutable() else {
                    throw TranscriptionError.backendMissing(.foundryCliNotFound)
                }
                return try await AuxiliaryOperations.shared.run {
                    try await FoundrySpeechModelCatalog.download(alias: alias, cliURL: cli)
                    try Task.checkCancellation()
                    return try await FoundrySpeechModelCatalog.list(cliURL: cli)
                }
            })
    }

    @Published private(set) var models: [FoundrySpeechModelChoice] = []
    @Published private(set) var catalogLoaded = false
    @Published private(set) var isDownloading = false
    @Published private(set) var status = "Checking Foundry Local model cache…"
    private let operations: Operations
    private var selectedAlias: String
    private var problem: String?
    private var owner: UUID?
    private var task: Task<Void, Never>?

    init(selectedAlias: String, operations: Operations = .live) {
        self.selectedAlias = selectedAlias
        self.operations = operations
    }

    deinit {
        task?.cancel()
    }

    func select(_ alias: String) {
        selectedAlias = alias
        updateStatus()
    }

    var canDownload: Bool {
        !isDownloading && catalogLoaded && models.first(where: { $0.alias == selectedAlias })?.isCached == false
    }

    @discardableResult
    func refresh() -> Task<Void, Never> {
        start(download: false)
    }

    @discardableResult
    func download() -> Task<Void, Never> {
        start(download: true)
    }

    func cancel() {
        owner = nil
        task?.cancel()
        task = nil
        isDownloading = false
        updateStatus()
    }

    private func start(download: Bool) -> Task<Void, Never> {
        cancel()
        let ticket = UUID()
        owner = ticket
        let alias = selectedAlias
        isDownloading = download
        status =
            download ? "Downloading the selected model. This may take a while." : "Checking Foundry Local model cache…"
        let work = Task { @MainActor [weak self, operations] in
            defer {
                if let self, self.owner == ticket {
                    self.isDownloading = false
                    self.owner = nil
                    self.task = nil
                }
            }
            do {
                try Task.checkCancellation()
                let models = try await (download ? operations.download(alias) : operations.list())
                try Task.checkCancellation()
                guard let self, self.owner == ticket else { return }
                self.models = models
                self.catalogLoaded = true
                self.problem = nil
                self.isDownloading = false
                self.updateStatus()
            } catch {
                guard let self, self.owner == ticket else { return }
                if error is CancellationError || Task.isCancelled {
                    self.status = download ? "The model download was cancelled." : "The model list check was cancelled."
                } else if error as? AuxiliaryOperations.Refusal == .closed {
                    if !download { self.problem = "Scribe is quitting." }
                    self.status = "Scribe is quitting. The selected model has not changed."
                } else if error as? TranscriptionError == .backendMissing(.foundryCliNotFound) {
                    self.catalogLoaded = false
                    self.problem = FoundryLocalSetupText.missing
                    self.status = FoundryLocalSetupText.missing + " Model availability cannot be checked."
                } else if download {
                    self.status = "Foundry Local could not download this model. Check Foundry Local and try again."
                } else {
                    self.catalogLoaded = false
                    self.problem = "Foundry Local could not list speech models."
                    self.status = "Foundry Local could not list speech models. Your saved selection is unchanged."
                }
            }
        }
        task = work
        return work
    }

    private func updateStatus() {
        guard !isDownloading else { return }
        guard catalogLoaded else {
            status =
                problem.map { "\($0) Your saved selection is unchanged." }
                ?? "Waiting for Foundry Local's speech model list."
            return
        }
        guard let choice = models.first(where: { $0.alias == selectedAlias }) else {
            status =
                "Foundry Local does not list this saved alias. It is preserved; choose a listed model to use or download."
            return
        }
        if choice.isCached == true {
            status = "This model is downloaded and ready."
        } else if choice.isCached == false {
            status =
                selectedAlias == TranscriptionEngine.defaultFoundryModelAlias
                ? "The default model is not downloaded. Foundry may download it on first use, as before; you can download it here."
                : "This newly selected model is not downloaded. Download it before dictating."
        } else {
            status = "Foundry Local did not report whether this model is downloaded."
        }
    }
}
