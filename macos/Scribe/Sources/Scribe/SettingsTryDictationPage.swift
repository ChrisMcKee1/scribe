import SwiftUI


struct SettingsTryDictationPage: View {
    let pipelineReportStore: PipelineReportStore

    var body: some View {
        SettingsPage(title: "Try dictation", subtitle: "Check that dictation works, and see what Scribe changed.") {
            SettingsGroupHeader("Result")
            SettingsCard { PlaygroundSettingsTab(pipelineReportStore: pipelineReportStore) }
        }
    }
}

// MARK: - Playground tab

/// Live view of the last dictation run through the full pipeline: raw recognition, replacement
/// highlights, and per-step timings. Mirrors Windows' Playground panel (see
/// src/Scribe.App/Settings/SettingsWindow.xaml, "Playground" section), which is populated from
/// `DictationController.PipelineReported`. On macOS the analogous signal is `PipelineReportStore`,
/// published by `DictationController` after every real dictation (hotkey or the
/// "Start Test Dictation" menu item). There is no separate "Run" button here because macOS's
/// push-to-talk hotkey already works regardless of which window is focused, so simply dictating
/// normally while this tab is open is enough to see a report land.
struct PlaygroundSettingsTab: View {
    @ObservedObject var pipelineReportStore: PipelineReportStore

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Latest dictation")
                    .font(.headline)
                Text(
                    """
                    Dictate normally (hotkey or \"Start Test Dictation\") while this tab is open to see the raw \
                    transcript, dictionary/snippet replacements, and per-step timings for the most recent run.
                    """
                )
                .foregroundStyle(.secondary)

                if let report = pipelineReportStore.latest {
                    if let failureStage = report.failureStage {
                        Label(
                            "Failed at \(failureStage.rawValue): \(report.failureReason ?? "unknown error")",
                            systemImage: "exclamationmark.triangle"
                        )
                        .foregroundStyle(.red)
                    }

                    GroupBox("Raw Recognition") {
                        Text(report.rawText?.isEmpty == false ? report.rawText! : "(no speech recognized)")
                            .font(.system(.body, design: .monospaced))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.vertical, 4)
                    }

                    if let sent = report.sentText {
                        GroupBox("Sent to AI cleanup (vocabulary applied)") {
                            Text(sent)
                                .font(.system(.body, design: .monospaced))
                                .textSelection(.enabled)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(.vertical, 4)
                        }
                    }

                    GroupBox("Processed Text (Replacements Highlighted)") {
                        highlightedText(for: report.postProcessing)
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.vertical, 4)
                    }

                    GroupBox("Timings") {
                        VStack(alignment: .leading, spacing: 4) {
                            timingRow("Capture", report.captureDuration)
                            timingRow("Speech Recognition (Decode)", report.decodeDuration)
                            if let cleanupDuration = report.cleanupDuration {
                                timingRow(
                                    report.cleanupApplied ? "AI cleanup" : "AI cleanup (failed, raw text used)",
                                    cleanupDuration)
                            }
                            timingRow("Dictionary / snippets", report.postProcessingDuration)
                            timingRow("Text Insertion", report.injectionDuration)
                            Divider()
                            timingRow("Total", report.totalDuration)
                            if let rtf = report.realTimeFactor {
                                Text("Real-time factor: \(String(format: "%.2fx", rtf))")
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.vertical, 4)
                    }

                    // No row for voice activity detection: macOS runs none as a step of its own (silence auto-stop
                    // is `SilenceAutoStopTracker`, inside capture), so there is nothing separate to time.
                } else {
                    Text("No dictation captured yet this session.")
                        .foregroundStyle(.secondary)
                        .padding(.top, 8)
                }
            }
            .padding(.vertical, 8)
        }
    }

    private func timingRow(_ label: String, _ duration: TimeInterval?) -> some View {
        HStack {
            Text(label)
            Spacer()
            if let duration {
                Text(String(format: "%.0f ms", duration * 1_000))
                    .foregroundStyle(.secondary)
            } else {
                Text("n/a")
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func highlightedText(for result: TextPostProcessingResult?) -> Text {
        guard let result, !result.text.isEmpty else {
            return Text("(no text)")
        }
        guard !result.replacements.isEmpty else {
            return Text(result.text).font(.system(.body, design: .monospaced))
        }

        let nsText = result.text as NSString
        var segments: [Text] = []
        var cursor = 0
        for replacement in result.replacements.sorted(by: { $0.start < $1.start }) {
            guard replacement.start >= cursor, replacement.start + replacement.length <= nsText.length else { continue }
            if replacement.start > cursor {
                segments.append(
                    Text(nsText.substring(with: NSRange(location: cursor, length: replacement.start - cursor))))
            }
            let highlighted = nsText.substring(with: NSRange(location: replacement.start, length: replacement.length))
            let color: Color = replacement.kind == .dictionary ? .blue : .green
            segments.append(Text(highlighted).foregroundColor(color).underline())
            cursor = replacement.start + replacement.length
        }
        if cursor < nsText.length {
            segments.append(Text(nsText.substring(from: cursor)))
        }

        return segments.reduce(Text("")) { partial, next in partial + next }
            .font(.system(.body, design: .monospaced))
    }
}

