import SwiftUI


struct SettingsDiagnosticsPage: View {
    let persistenceStore: PersistenceStore

    var body: some View {
        SettingsPage(title: "Diagnostics", subtitle: "Get help, see what went wrong, and check how fast dictation runs.") {
            SettingsGroupHeader("Performance")
            SettingsCard { DiagnosticsSettingsTab(persistenceStore: persistenceStore) }
        }
    }
}

// MARK: - Diagnostics tab

/// Read-only performance panel over `dictation_history`, mirroring Windows' Diagnostics tab
/// (P50/P95 decode latency, real-time factor). Computed with `DictationStats.compute`, the same
/// aggregation used by `Scribe --diagnostics` for headless verification.
struct DiagnosticsSettingsTab: View {
    @StateObject private var model: DiagnosticsSettingsModel

    init(persistenceStore: PersistenceStore) {
        _model = StateObject(wrappedValue: DiagnosticsSettingsModel(access: .live(persistenceStore)))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("Performance")
                    .font(.headline)
                Spacer()
                Picker("Window", selection: $model.windowDays) {
                    Text("24 hours").tag(1.0)
                    Text("7 days").tag(7.0)
                    Text("30 days").tag(30.0)
                }
                .pickerStyle(.segmented)
                .frame(width: 260)
                .onChange(of: model.windowDays) { _ in
                    Task { await model.reload() }
                }
            }

            if let errorMessage = model.errorMessage {
                Text(errorMessage)
                    .foregroundStyle(.red)
            }

            if let snapshot = model.stats {
                ScrollView {
                    VStack(alignment: .leading, spacing: 12) {
                        if let coverageNote = model.coverageNote {
                            Text(coverageNote)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        metricRow(label: "Dictations", value: "\(snapshot.count)")
                        metricRow(
                            label: "Total audio",
                            value: String(
                                format: "%.1f s (longest %.1f s)", snapshot.totalAudioSeconds,
                                snapshot.longestAudioSeconds))

                        Divider()

                        if let decodeMs = snapshot.decodeMs {
                            Text("Decode latency (ms)")
                                .font(.subheadline.bold())
                            metricRow(label: "Average", value: String(format: "%.0f", decodeMs.average))
                            metricRow(label: "P50", value: String(format: "%.0f", decodeMs.p50))
                            metricRow(label: "P95", value: String(format: "%.0f", decodeMs.p95))
                            metricRow(
                                label: "Min / Max", value: String(format: "%.0f / %.0f", decodeMs.min, decodeMs.max))

                            Divider()

                            Text("Real-time factor")
                                .font(.subheadline.bold())
                            metricRow(label: "Fastest", value: String(format: "%.3fx", snapshot.fastestRtf))
                            metricRow(label: "P50", value: String(format: "%.3fx", snapshot.rtfP50))
                            metricRow(label: "P95", value: String(format: "%.3fx", snapshot.rtfP95))
                        } else {
                            Text(
                                "No timed dictations yet. Decode latency is recorded starting with the next dictation."
                            )
                            .foregroundStyle(.secondary)
                        }

                        if let cleanupMs = snapshot.cleanupMs {
                            Divider()
                            Text("AI cleanup latency (ms)")
                                .font(.subheadline.bold())
                            metricRow(label: "Average", value: String(format: "%.0f", cleanupMs.average))
                            metricRow(
                                label: "Min / Max", value: String(format: "%.0f / %.0f", cleanupMs.min, cleanupMs.max))
                        }
                    }
                }
            } else {
                Text("No dictations in this window yet.")
                    .foregroundStyle(.secondary)
            }

            Spacer()
        }
        .onAppear {
            Task { await model.reload() }
        }
    }

    private func metricRow(label: String, value: String) -> some View {
        HStack {
            Text(label)
                .foregroundStyle(.secondary)
            Spacer()
            Text(value)
                .monospacedDigit()
        }
    }
}

