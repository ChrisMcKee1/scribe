import SwiftUI

struct SettingsTryDictationPage: View {
    let pipelineReportStore: PipelineReportStore

    var body: some View {
        SettingsPage(title: "Try dictation", subtitle: "Check that dictation works, and see what Scribe changed.") {
            VStack(alignment: .leading, spacing: 14) {
                SettingsGroupHeader("Try it")
                SettingsCard { TryDictationInputCard() }

                SettingsGroupHeader("Result")
                SettingsCard { PlaygroundSettingsTab(pipelineReportStore: pipelineReportStore) }
            }
        }
    }
}

private struct TryDictationInputCard: View {
    @State private var text = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Click in the box, hold your dictation shortcut, and speak.").cardTitle()
            Text("Try a short sentence first, such as: Scribe typed this on my Mac.")
                .cardDescription()
            TextEditor(text: $text)
                .font(.body)
                .frame(minHeight: 150, maxHeight: 280)
                .padding(6)
                .background(
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .fill(Color(nsColor: .textBackgroundColor)))
                .overlay(
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .stroke(Color(nsColor: .separatorColor).opacity(0.4), lineWidth: 1))
            HStack {
                Spacer()
                Button("Clear") { text = "" }
                    .disabled(text.isEmpty)
            }
        }
    }
}

// MARK: - Playground tab

/// Live view of the last dictation run through the full pipeline: raw recognition, replacement
/// highlights, and per-step timings. Mirrors Windows' Try dictation result panel, populated from
/// `DictationController.PipelineReported`. On macOS the analogous signal is `PipelineReportStore`,
/// published by `DictationController` after every real dictation.
struct PlaygroundSettingsTab: View {
    @ObservedObject var pipelineReportStore: PipelineReportStore

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            if let report = pipelineReportStore.latest {
                summary(for: report)

                resultSection(title: "What Scribe heard") {
                    monospaceText(report.rawText?.isEmpty == false ? report.rawText! : "(no speech recognized)")
                }

                resultSection(title: "What Scribe typed") {
                    monospaceText(report.finalText?.isEmpty == false ? report.finalText! : report.postProcessing?.text ?? "(no text)")
                }

                resultSection(title: changesTitle(for: report)) {
                    highlightedText(for: report.postProcessing)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }

                DisclosureGroup("Timing details") {
                    VStack(alignment: .leading, spacing: 6) {
                        timingRow("Recording", report.captureDuration)
                        timingRow("Speech recognition", report.decodeDuration)
                        if let cleanupDuration = report.cleanupDuration {
                            timingRow(
                                report.cleanupApplied ? "AI cleanup" : "AI cleanup, raw text used",
                                cleanupDuration)
                        }
                        timingRow("Dictionary and snippets", report.postProcessingDuration)
                        timingRow("Typing", report.injectionDuration)
                        Divider()
                        timingRow("Processing, all steps", report.totalDuration)
                        if let rtf = report.realTimeFactor {
                            Text("Real-time factor: \(String(format: "%.2fx", rtf))")
                                .foregroundStyle(.secondary)
                        }
                    }
                    .padding(.top, 8)
                }
            } else {
                Text("Your result appears here after you dictate into the box.")
                    .cardDescription()
            }
        }
    }

    private func summary(for report: PipelineReport) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: report.failureStage == nil ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                .foregroundStyle(report.failureStage == nil ? Color.green : Color.red)
            VStack(alignment: .leading, spacing: 2) {
                if let failureStage = report.failureStage {
                    Text("Failed at \(failureStage.rawValue)").cardTitle()
                    Text(report.failureReason ?? "Unknown error")
                        .cardDescription()
                } else {
                    Text("Dictation captured").cardTitle()
                    Text("Review what Scribe heard, what it typed, and how long each step took.")
                        .cardDescription()
                }
            }
        }
    }

    private func resultSection<Content: View>(title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.subheadline.weight(.semibold))
            content()
        }
    }

    private func monospaceText(_ value: String) -> some View {
        Text(value)
            .font(.system(.body, design: .monospaced))
            .textSelection(.enabled)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.vertical, 4)
    }

    private func changesTitle(for report: PipelineReport) -> String {
        let replacements = report.postProcessing?.replacements.count ?? 0
        return replacements == 0 ? "No dictionary or snippet changes" : "Dictionary and snippet changes"
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
