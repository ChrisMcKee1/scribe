import AppKit
import SwiftUI

struct SettingsDiagnosticsPage: View {
    let persistenceStore: PersistenceStore

    var body: some View {
        SettingsPage(
            title: "Diagnostics", subtitle: "Get help, see what went wrong, and check how fast dictation runs."
        ) {
            DiagnosticsSettingsTab(persistenceStore: persistenceStore)
        }
    }
}

// MARK: - Diagnostics tab

/// Read-only performance panel over `dictation_history`, mirroring Windows' Diagnostics tab
/// P50 and P95 decode latency and real-time factor. Computed with `DictationStats.compute`, the same
/// aggregation used by `Scribe --diagnostics` for headless verification.
struct DiagnosticsSettingsTab: View {
    @StateObject private var model: DiagnosticsSettingsModel
    let persistenceStore: PersistenceStore

    private static let newIssueURL = URL(string: "https://github.com/x3nc0n/scribe/issues/new")!

    init(persistenceStore: PersistenceStore) {
        self.persistenceStore = persistenceStore
        _model = StateObject(wrappedValue: DiagnosticsSettingsModel(access: .live(persistenceStore)))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            SettingsGroupHeader("Get help")
            SettingsCard { helpCard }

            SettingsGroupHeader("Diagnostic data")
            SettingsCard { diagnosticDataCard }

            SettingsGroupHeader("Speed")
            SettingsCard { speedCard }
            SettingsCard { speedDetailsCard }

            SettingsGroupHeader("This Mac")
            SettingsCard { thisMacCard }

            SettingsGroupHeader("Where Scribe keeps your data")
            SettingsCard { dataFileCard }
        }
        .onAppear {
            Task { await model.reload() }
        }
    }

    private var helpCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Report a problem").cardTitle()
                    Text(
                        "Opens GitHub to report a problem or suggest a feature. Do not include dictations, recordings, keys or secrets in a public report."
                    )
                    .cardDescription()
                }
                Spacer()
                Button("Report a problem") {
                    NSWorkspace.shared.open(Self.newIssueURL)
                }
            }
            Divider()
            VStack(alignment: .leading, spacing: 4) {
                Text("Save diagnostics").cardTitle()
                Text(
                    "The macOS port does not create a diagnostics zip yet. Use this page for timings, system shape and local paths, and read any details before sharing them."
                )
                .cardDescription()
            }
        }
    }

    private var diagnosticDataCard: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Logs").cardTitle()
            Text(
                "Scribe for macOS writes app events to Apple unified logging with subsystem com.scribe.macos. Dictation text is not logged by Scribe."
            )
            .cardDescription()
            Text("AI cleanup problems").cardTitle()
                .padding(.top, 8)
            Text(
                "Cleanup failures are surfaced on the dictation result and in the pipeline report for this session. There is not yet a stored macOS failure list like Windows has."
            )
            .cardDescription()
        }
    }

    private var speedCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("How long each step takes").cardTitle()
                    Text(speedSummaryText).cardDescription()
                    if let coverageNote = model.coverageNote {
                        Text(coverageNote).cardDescription()
                    }
                    if let errorMessage = model.errorMessage {
                        Text(errorMessage)
                            .font(.caption)
                            .foregroundStyle(.red)
                    }
                }
                Spacer()
                HStack(spacing: 8) {
                    Text("Window")
                        .font(.callout.weight(.semibold))
                        .fixedSize()
                    Picker("Window", selection: $model.windowDays) {
                        Text("24 hours").tag(1.0)
                        Text("7 days").tag(7.0)
                        Text("30 days").tag(30.0)
                    }
                    .labelsHidden()
                    .pickerStyle(.segmented)
                    .frame(width: 260)
                }
                .onChange(of: model.windowDays) { _ in
                    Task { await model.reload() }
                }
            }

            if let snapshot = model.stats {
                HStack(alignment: .top, spacing: 16) {
                    metricBlock(
                        title: "Speech recognition", typical: snapshot.decodeMs?.p50, p95: snapshot.decodeMs?.p95)
                    metricBlock(title: "AI cleanup", typical: snapshot.cleanupMs?.p50, p95: snapshot.cleanupMs?.p95)
                    metricBlock(title: "Both", typical: snapshot.combinedMs?.p50, p95: snapshot.combinedMs?.p95)
                }
            }
        }
    }

    private var speedDetailsCard: some View {
        DisclosureGroup("Speed details") {
            VStack(alignment: .leading, spacing: 12) {
                if let snapshot = model.stats {
                    Text(
                        "\(snapshot.count) dictation(s), \(String(format: "%.1f s", snapshot.totalAudioSeconds)) of audio, longest \(String(format: "%.1f s", snapshot.longestAudioSeconds))."
                    )
                    .cardDescription()
                    Text(
                        "Best real-time factor: \(String(format: "%.3fx", snapshot.fastestRtf)). P50 \(String(format: "%.3fx", snapshot.rtfP50)), P95 \(String(format: "%.3fx", snapshot.rtfP95))."
                    )
                    .cardDescription()
                    metricSummary("Speech recognition", snapshot.decodeMs)
                    metricSummary("AI cleanup", snapshot.cleanupMs)
                    metricSummary("Recognition plus AI cleanup", snapshot.combinedMs)
                } else {
                    Text("No dictations in this window yet.").cardDescription()
                }
            }
            .padding(.top, 8)
        }
    }

    private var speedSummaryText: String {
        guard let snapshot = model.stats else {
            return "No dictations in the selected window yet."
        }
        return "Based on \(snapshot.count) dictation(s) in the selected window."
    }

    private var thisMacCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(systemSummary).cardDescription()
            Text(
                "Speech recognition uses the Foundry Local command-line recognizer when available, with whisper.cpp as a developer fallback."
            )
            .cardDescription()
        }
    }

    private var dataFileCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Scribe data file").cardTitle()
            Text(
                "Never send or share this file. It holds your dictation history, dictionary, snippets, app profiles and saved cleanup settings."
            )
            .cardDescription()
            HStack {
                Text(persistenceStore.databaseURL.path)
                    .font(.system(.footnote, design: .monospaced))
                    .textSelection(.enabled)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer()
                Button("Copy") { copy(persistenceStore.databaseURL.path) }
                Button("Reveal in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting([persistenceStore.databaseURL])
                }
            }
        }
    }

    private var systemSummary: String {
        let info = ProcessInfo.processInfo
        let memoryGB = Double(info.physicalMemory) / 1_073_741_824.0
        return
            "\(info.operatingSystemVersionString), \(info.processorCount) processor(s), \(String(format: "%.1f GB", memoryGB)) memory."
    }

    private func metricBlock(title: String, typical: Double?, p95: Double?) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.subheadline.weight(.semibold))
            Text(typical.map(formatMs) ?? "n/a")
                .font(.title3.weight(.semibold))
                .monospacedDigit()
            Text("Typical")
                .cardDescription()
            Text(p95.map { "19 in 20 within \(formatMs($0))" } ?? "No data yet")
                .cardDescription()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func metricSummary(_ title: String, _ summary: DictationStats.MetricSummary?) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).cardTitle()
            if let summary {
                HStack {
                    detailMetric("average", summary.average)
                    detailMetric("fastest", summary.min)
                    detailMetric("slowest", summary.max)
                }
            } else {
                Text("No runs in this period yet.").cardDescription()
            }
        }
    }

    private func detailMetric(_ label: String, _ value: Double) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(formatMs(value))
                .font(.callout.weight(.semibold))
                .monospacedDigit()
            Text(label).cardDescription()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func formatMs(_ value: Double) -> String {
        "\(String(format: "%.0f", value)) ms"
    }

    private func copy(_ value: String) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(value, forType: .string)
    }
}
