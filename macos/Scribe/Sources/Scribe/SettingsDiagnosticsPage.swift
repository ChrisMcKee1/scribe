import AppKit
import SwiftUI

struct SettingsDiagnosticsPage: View {
    let persistenceStore: PersistenceStore
    let pipelineReportStore: PipelineReportStore

    var body: some View {
        SettingsPage(
            title: "Diagnostics", subtitle: "Get help, see what went wrong, and check how fast dictation runs."
        ) {
            DiagnosticsSettingsTab(persistenceStore: persistenceStore, pipelineReportStore: pipelineReportStore)
        }
    }
}

// MARK: - Diagnostics tab

/// Read-only performance panel over `dictation_history`, mirroring Windows' Diagnostics tab
/// P50 and P95 decode latency and real-time factor. Computed with `DictationStats.compute`, the same
/// aggregation used by `Scribe --diagnostics` for headless verification.
struct DiagnosticsSettingsTab: View {
    @StateObject private var model: DiagnosticsSettingsModel
    @ObservedObject var pipelineReportStore: PipelineReportStore
    let persistenceStore: PersistenceStore

    init(persistenceStore: PersistenceStore, pipelineReportStore: PipelineReportStore) {
        self.persistenceStore = persistenceStore
        self.pipelineReportStore = pipelineReportStore
        _model = StateObject(wrappedValue: DiagnosticsSettingsModel(access: .live(persistenceStore)))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            SettingsGroupHeader("Get help")
            SettingsCard(searchID: "diagnostics.help") { helpCard }

            SettingsGroupHeader("Diagnostic data")
            SettingsCard(searchID: "diagnostics.data") { diagnosticDataCard }

            SettingsGroupHeader("Speed")
            SettingsCard(searchID: "diagnostics.speed") { speedCard }
            SettingsCard { speedDetailsCard }

            SettingsGroupHeader("This Mac")
            SettingsCard(searchID: "diagnostics.mac") { thisMacCard }

            SettingsGroupHeader("Where Scribe keeps your data")
            SettingsCard(searchID: "diagnostics.data-file") { dataFileCard }
        }
        .task(id: model.windowDays) {
            await model.reload()
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
                    NSWorkspace.shared.open(ScribeRepository.newIssueURL)
                }
            }
            Divider()
            VStack(alignment: .leading, spacing: 4) {
                Text("Save diagnostics").cardTitle()
                Text(
                    "Save recent shape-only app logs and a system summary. The archive excludes dictations, recordings, saved settings and keys. Review it before sharing."
                )
                .cardDescription()
                SaveDiagnosticsButton()
            }
        }
    }

    private var diagnosticDataCard: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Logs").cardTitle()
            Text(
                "Scribe writes shape-only app events to Apple unified logging and local daily files. Logs are kept for seven days, with soft limits of 16 MB a day and 64 MB total. Dictation text is never included."
            )
            .cardDescription()
            Text("Recent dictation problems").cardTitle()
                .padding(.top, 8)
            Text(
                "The newest \(PipelineReportStore.problemLimit) problems from this session, newest first. Only the time and outcome are kept here, not your words or service error details. Clearing history clears this list. It is not saved or exported."
            )
            .cardDescription()
            if pipelineReportStore.problems.isEmpty {
                Text("No dictation problems reported this session.")
                    .cardDescription()
            } else {
                ForEach(pipelineReportStore.problems) { problem in
                    Divider()
                    Text(problem.capturedAt, style: .time)
                        .font(.callout.weight(.semibold))
                    ForEach(problem.messages, id: \.self) { message in
                        Text(message).cardDescription()
                    }
                }
            }
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
