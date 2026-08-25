import AppKit
import SwiftUI

/// About tab: version, privacy stance, support/source links, GitHub star, and local data
/// locations. Direct port of the intent behind Windows' `SectionAbout` in `SettingsWindow.xaml`,
/// adapted to macOS conventions (Finder rather than File Explorer, no Microsoft Store share
/// card since Scribe for macOS isn't Store-distributed).
struct AboutView: View {
    let persistenceStore: PersistenceStore

    private static let repoURL = URL(string: "https://github.com/x3nc0n/scribe")!
    private static let privacyPolicyURL = URL(string: "https://github.com/x3nc0n/scribe/blob/main/PRIVACY.md")!
    private static let newIssueURL = URL(string: "https://github.com/x3nc0n/scribe/issues/new")!

    @State private var updateChecker = UpdateChecker()
    @State private var updateCheckResult: UpdateCheckResult?
    @State private var isCheckingForUpdate = false

    private var appVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "unknown"
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                headerCard
                updateCard
                privacyCard
                starCard
                supportCard
                dataLocationsCard
            }
            .padding(.vertical, 4)
        }
    }

    private var headerCard: some View {
        card {
            HStack(alignment: .center, spacing: 18) {
                RoundedRectangle(cornerRadius: 18)
                    .fill(Color.accentColor.opacity(0.18))
                    .frame(width: 72, height: 72)
                    .overlay(
                        Image(systemName: "mic.fill")
                            .font(.system(size: 30))
                            .foregroundStyle(Color.accentColor))
                VStack(alignment: .leading, spacing: 3) {
                    Text("Scribe AI")
                        .font(.title2.bold())
                    Text("Version \(appVersion)")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                    Text("Private push-to-talk voice dictation for macOS")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                Spacer()
            }
        }
    }

    private var updateCard: some View {
        card {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Updates")
                        .font(.headline)
                    Text(updateStatusText)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                VStack(alignment: .trailing, spacing: 8) {
                    Button(isCheckingForUpdate ? "Checking..." : "Check for Updates") {
                        checkForUpdate()
                    }
                    .disabled(isCheckingForUpdate)
                    if case .updateAvailable(_, _, let url) = updateCheckResult {
                        Button("Download latest") {
                            NSWorkspace.shared.open(url)
                        }
                        .buttonStyle(.borderedProminent)
                    }
                }
            }
        }
    }

    private var updateStatusText: String {
        switch updateCheckResult {
        case .none:
            return "Scribe has no auto-updater yet; check GitHub Releases manually for a newer version."
        case .upToDate(let current):
            return "You're up to date (version \(current))."
        case .updateAvailable(let current, let latest, _):
            return "Version \(latest) is available (you have \(current))."
        case .failed(let message):
            return message
        }
    }

    private func checkForUpdate() {
        isCheckingForUpdate = true
        let version = appVersion
        Task {
            let result = await updateChecker.checkForUpdate(currentVersion: version)
            await MainActor.run {
                updateCheckResult = result
                isCheckingForUpdate = false
            }
        }
    }

    private var privacyCard: some View {
        card {
            VStack(alignment: .leading, spacing: 8) {
                Text("Private by design")
                    .font(.headline)
                    .foregroundStyle(Color.accentColor)
                Text("Speech recognition runs on this Mac, and audio never leaves it. Cloud AI is optional and sends text only when you enable or invoke a remote provider feature.")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                Button("Read privacy policy") {
                    NSWorkspace.shared.open(Self.privacyPolicyURL)
                }
            }
        }
    }

    private var starCard: some View {
        card {
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Love Scribe?")
                        .font(.headline)
                    Text("A GitHub star helps other people discover private, offline dictation.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button("Open GitHub to star") {
                    NSWorkspace.shared.open(Self.repoURL)
                }
                .buttonStyle(.borderedProminent)
            }
        }
    }

    private var supportCard: some View {
        card {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Support and source")
                        .font(.headline)
                    Text("Report a problem, request a feature, or inspect the code that runs on your Mac. Never put transcripts, audio, credentials, or other sensitive information in a public issue.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                VStack(spacing: 8) {
                    Button("Report an issue") {
                        NSWorkspace.shared.open(Self.newIssueURL)
                    }
                    Button("View source") {
                        NSWorkspace.shared.open(Self.repoURL)
                    }
                }
            }
        }
    }

    private var dataLocationsCard: some View {
        card {
            VStack(alignment: .leading, spacing: 8) {
                Text("Where your data is stored")
                    .font(.headline)
                Text("Scribe keeps its local data file at this location. Copy the path to paste it elsewhere, or reveal it in Finder.")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)

                Text("Scribe data file")
                    .font(.subheadline.bold())
                    .padding(.top, 4)
                Text("One database holding your dictation history, dictionary, snippets, profiles and settings. Never send or post this file: it contains everything you have dictated.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)

                HStack {
                    Text(persistenceStore.databaseURL.path)
                        .font(.system(.footnote, design: .monospaced))
                        .textSelection(.enabled)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer()
                    Button("Copy") {
                        let pasteboard = NSPasteboard.general
                        pasteboard.clearContents()
                        pasteboard.setString(persistenceStore.databaseURL.path, forType: .string)
                    }
                    Button("Reveal in Finder") {
                        NSWorkspace.shared.activateFileViewerSelecting([persistenceStore.databaseURL])
                    }
                }
            }
        }
    }

    private func card<Content: View>(@ViewBuilder content: () -> Content) -> some View {
        content()
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 12)
                    .fill(Color(nsColor: .controlBackgroundColor)))
    }
}
