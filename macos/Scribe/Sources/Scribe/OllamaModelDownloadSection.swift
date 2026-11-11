import Foundation
import SwiftUI

/// The Download another model form under Ollama on the AI cleanup page, ported from Windows'
/// `SettingsWindow.OllamaDownloads`. Nothing downloads by browsing: only Download starts one. A finished download
/// refreshes Ollama's model list without choosing the new model, and only Use model, in the question that follows,
/// stores it as the model AI cleanup uses. Cancel, choosing another app or place, turning AI cleanup off or closing
/// Settings stops the download, and progress that arrives after that is ignored.
@MainActor
final class OllamaModelDownloadModel: ObservableObject {
    typealias Download =
        @Sendable (
            _ model: String,
            _ endpoint: String,
            _ progress: @escaping @Sendable (OllamaDownloadProgress) async -> Void
        ) async throws -> Void
    /// Reads Ollama's model list again and returns the reading it made.
    typealias Refresh = @MainActor @Sendable (_ endpoint: String) async -> LocalServerState?

    @Published var modelName = ""
    @Published private(set) var isDownloading = false
    @Published private(set) var message: String?
    /// The current file's progress, 0 to 100, while Ollama reports one with a known size.
    @Published private(set) var percent: Double?
    /// The downloaded model waiting for Use model or Keep current model.
    @Published private(set) var pendingChoice: String?

    static let live: Download = { model, endpoint, progress in
        try await OllamaModelDownloader().download(model, from: endpoint, progress: progress)
    }

    private let download: Download
    /// The download whose results may still land. Replaced or cleared when it ends or is stopped, so a late answer
    /// from an earlier one changes nothing.
    private var owner: UUID?
    private nonisolated let running = RunningOllamaDownload()
    private var closeObservation: SettingsNotificationObservation?

    init(download: @escaping Download = OllamaModelDownloadModel.live, center: NotificationCenter = .default) {
        self.download = download
        closeObservation = SettingsNotificationObservation(
            SettingsWindowController.willCloseNotification, center: center
        ) { [weak self] in
            self?.stop()
        }
    }

    deinit {
        running.cancel()
    }

    /// Starts downloading the typed name from the Ollama at `endpoint`. Refused while a download runs or a downloaded
    /// model waits for its question; a name the form refuses sends nothing.
    func start(endpoint: String?, refresh: @escaping Refresh) {
        guard !isDownloading, pendingChoice == nil else {
            return
        }
        let request = OllamaModelDownload.validate(modelName)
        guard let model = request.model else {
            message = request.error
            return
        }
        guard let endpoint else {
            message = OllamaModelDownload.failure(OllamaDownloadError.notOllama)
            return
        }

        let owner = UUID()
        self.owner = owner
        isDownloading = true
        percent = nil
        message = OllamaModelDownload.preparing
        let download = download
        running.replace(
            Task { [weak self] in
                do {
                    try await download(model, endpoint) { [weak self] value in
                        await self?.report(value, owner: owner)
                    }
                } catch {
                    self?.end(owner, message: OllamaModelDownloadModel.notice(for: error))
                    return
                }
                await self?.finish(model, endpoint: endpoint, owner: owner, refresh: refresh)
            })
    }

    /// Stops the download. Ollama keeps what it downloaded, so Download resumes it.
    func cancel() {
        guard isDownloading else {
            return
        }
        running.cancel()
    }

    /// Stops the download and drops a waiting question, for when the form goes away.
    func stop() {
        guard isDownloading || pendingChoice != nil else {
            return
        }
        running.cancel()
        message = isDownloading ? OllamaModelDownload.canceled : OllamaModelDownload.declined
        owner = nil
        isDownloading = false
        percent = nil
        pendingChoice = nil
    }

    /// Use model: stores the downloaded model as AI cleanup's choice through `apply`, and nothing else.
    func confirm(_ apply: (String) -> Void) {
        guard let model = pendingChoice else {
            return
        }
        pendingChoice = nil
        guard let name = OllamaModelDownload.validate(model).model else {
            message = OllamaModelDownload.declined
            return
        }
        apply(name)
        message = OllamaModelDownload.saved
    }

    /// Keep current model.
    func decline() {
        guard pendingChoice != nil else {
            return
        }
        pendingChoice = nil
        message = OllamaModelDownload.declined
    }

    private func report(_ value: OllamaDownloadProgress, owner: UUID) {
        guard self.owner == owner, isDownloading else {
            return
        }
        message = OllamaModelDownload.describe(value)
        percent = value.percent.map { min(max($0, 0), 100) }
    }

    private func finish(_ model: String, endpoint: String, owner: UUID, refresh: Refresh) async {
        guard self.owner == owner else {
            return
        }
        guard !Task.isCancelled else {
            end(owner, message: OllamaModelDownload.canceled)
            return
        }
        percent = nil
        let reading = await refresh(endpoint)
        guard self.owner == owner else {
            return
        }
        guard !Task.isCancelled else {
            end(owner, message: OllamaModelDownload.canceled)
            return
        }
        if let problem = OllamaModelDownload.completionProblem(reading, model: model) {
            end(owner, message: problem)
            return
        }
        end(owner, message: OllamaModelDownload.describe(OllamaDownloadProgress(stage: .completed)))
        pendingChoice = model
    }

    private func end(_ owner: UUID, message: String) {
        guard self.owner == owner else {
            return
        }
        self.owner = nil
        isDownloading = false
        percent = nil
        self.message = message
        running.clear()
    }

    fileprivate static func notice(for error: any Error) -> String {
        error is CancellationError ? OllamaModelDownload.canceled : OllamaModelDownload.failure(error)
    }
}

/// The running download's task, reachable from `deinit`, which is not on the main actor.
private final class RunningOllamaDownload: @unchecked Sendable {
    private let lock = NSLock()
    private var task: Task<Void, Never>?

    func replace(_ task: Task<Void, Never>) {
        lock.lock()
        let previous = self.task
        self.task = task
        lock.unlock()
        previous?.cancel()
    }

    func cancel() {
        lock.lock()
        let task = self.task
        self.task = nil
        lock.unlock()
        task?.cancel()
    }

    func clear() {
        lock.lock()
        task = nil
        lock.unlock()
    }
}

struct OllamaModelDownloadSection: View {
    @ObservedObject var download: OllamaModelDownloadModel
    let endpoint: String?
    let refresh: OllamaModelDownloadModel.Refresh
    let useModel: (String) -> Void

    var body: some View {
        DisclosureGroup {
            VStack(alignment: .leading, spacing: 8) {
                TextField("Model name", text: $download.modelName, prompt: Text("gemma4:12b"))
                    .onSubmit(start)
                    .disabled(download.isDownloading)
                    .accessibilityHint(OllamaModelDownload.downloadHint)
                Text(OllamaModelDownload.downloadHint)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Link("Browse Ollama's model catalog", destination: OllamaModelDownload.catalogURL)
                    .font(.caption)
                HStack {
                    Button("Download", action: start)
                        .disabled(download.isDownloading || download.pendingChoice != nil)
                        .accessibilityLabel("Download the named Ollama model")
                    if download.isDownloading {
                        Button("Cancel download") { download.cancel() }
                    }
                    Spacer()
                }
                if let percent = download.percent {
                    ProgressView(value: percent, total: 100)
                        .accessibilityLabel("Current model file download progress")
                }
                if let message = download.message {
                    Text(message)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .padding(.top, 4)
        } label: {
            VStack(alignment: .leading, spacing: 2) {
                Text("Download another model")
                Text("Find a model in Ollama's catalog and download it here.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .alert(
            "Use the downloaded model?",
            isPresented: Binding(get: { download.pendingChoice != nil }, set: { _ in }),
            presenting: download.pendingChoice
        ) { _ in
            Button("Use model") { download.confirm(useModel) }
            Button("Keep current model", role: .cancel) { download.decline() }
        } message: { model in
            Text(OllamaModelDownload.switchQuestion(model))
        }
    }

    private func start() {
        download.start(endpoint: endpoint, refresh: refresh)
    }
}
