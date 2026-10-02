import Charts
import SwiftUI


struct SettingsUsagePage: View {
    let persistenceStore: PersistenceStore
    let onChanged: @MainActor () -> Void

    var body: some View {
        SettingsPage(title: "Usage", subtitle: "How much you've dictated, and words you might add to your dictionary.") {
            SettingsGroupHeader("Usage")
            SettingsCard { UsageInsightsSettingsTab(persistenceStore: persistenceStore, onChanged: onChanged) }
        }
    }
}

// MARK: - Usage Insights tab

/// Local-only usage totals, a trend chart, top apps, and recurring-term mining, backed by
/// `UsageAnalyzer`. The AI summary section is the one part of this tab that leaves the device: it
/// is opt-in per generation (never automatic), available only while AI cleanup is on, and sends
/// only the aggregate `UsageInsight` payload (counts and dictionary-covered term labels other than
/// template-like replacements), never raw transcripts (see `UsageSummaryModel`). Mirrors Windows'
/// Usage Insights page, split across the totals/top-apps/recurring-terms/AI-summary PORTING-PLAN rows.
struct UsageInsightsSettingsTab: View {
    @StateObject private var model: UsageInsightsModel
    @StateObject private var summaryModel = UsageSummaryModel()

    init(persistenceStore: PersistenceStore, onChanged: @escaping @MainActor () -> Void) {
        _model = StateObject(wrappedValue: UsageInsightsModel(access: .live(persistenceStore), onChanged: onChanged))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("Usage")
                    .font(.headline)
                Spacer()
                Picker("Window", selection: $model.windowDays) {
                    Text("7 days").tag(7.0)
                    Text("30 days").tag(30.0)
                    Text("90 days").tag(90.0)
                }
                .pickerStyle(.segmented)
                .frame(width: 260)
                .onChange(of: model.windowDays) { _ in
                    summaryModel.reset()
                    Task { await model.reload() }
                }
            }

            if let loadError = model.loadError {
                Text(loadError)
                    .foregroundStyle(.red)
            }
            if let errorMessage = model.errorMessage {
                Text(errorMessage)
                    .foregroundStyle(.red)
            }
            if let statusMessage = model.statusMessage {
                Text(statusMessage)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            if let snapshot = model.snapshot {
                ScrollView {
                    VStack(alignment: .leading, spacing: 16) {
                        if let coverageNote = model.coverageNote {
                            Text(coverageNote)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        totalsSection(snapshot)
                        Divider()
                        trendSection(snapshot)
                        Divider()
                        topAppsSection(snapshot)
                        Divider()
                        termsSection(snapshot)
                        Divider()
                        aiSummarySection(snapshot)
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
        .onDisappear {
            summaryModel.cancelInFlight()
        }
    }

    // MARK: Totals

    private func totalsSection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Totals")
                .font(.subheadline.bold())
            metricRow(label: "Dictations", value: "\(snapshot.dictations)")
            metricRow(label: "Words", value: "\(snapshot.words)")
            metricRow(label: "Active days", value: "\(snapshot.activeDays)")
            metricRow(label: "Speech time", value: String(format: "%.1f min", snapshot.speechSeconds / 60.0))
            metricRow(label: "Average words / dictation", value: String(format: "%.1f", snapshot.averageWords))
        }
    }

    // MARK: Trend

    private func trendSection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(snapshot.granularity == .daily ? "Trend (daily)" : "Trend (weekly)")
                .font(.subheadline.bold())
            if snapshot.trend.isEmpty {
                Text("Not enough history to chart a trend yet.")
                    .foregroundStyle(.secondary)
            } else {
                Chart(snapshot.trend, id: \.start) { point in
                    BarMark(
                        x: .value(
                            "Period",
                            String(format: "%04d-%02d-%02d", point.start.year, point.start.month, point.start.day)),
                        y: .value("Dictations", point.dictations))
                }
                .frame(height: 160)
            }
        }
    }

    // MARK: Top apps

    private func topAppsSection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Top apps")
                .font(.subheadline.bold())
            if snapshot.topApps.isEmpty {
                Text("No app usage recorded yet.")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(snapshot.topApps, id: \.name) { app in
                    HStack {
                        Text(app.name)
                        Spacer()
                        Text("\(app.dictations) dictations, \(app.words) words")
                            .foregroundStyle(.secondary)
                            .monospacedDigit()
                    }
                }
            }
        }
    }

    // MARK: Recurring terms

    private func termsSection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Recurring terms")
                .font(.subheadline.bold())
            if snapshot.terms.isEmpty {
                Text("No recurring terms found yet.")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(snapshot.terms, id: \.text) { term in
                    HStack {
                        Text(term.text)
                        Text("(\(term.dictations) dictations, \(term.occurrences)x)")
                            .foregroundStyle(.secondary)
                            .font(.caption)
                        Spacer()
                        if term.covered {
                            Text("In dictionary")
                                .foregroundStyle(.secondary)
                                .font(.caption)
                        } else {
                            Button("Add to Dictionary") {
                                Task { await model.addTermToDictionary(term) }
                            }
                        }
                    }
                }
            }
        }
    }

    // MARK: AI summary (opt-in, sends aggregate counts only, never raw transcripts)

    private func aiSummarySection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("AI summary")
                .font(.subheadline.bold())
            Text(
                """
                Sends only aggregate totals and dictionary-covered term labels to your configured AI cleanup \
                provider. Novel terms, replacements that are templates in all but name (such as a signature \
                block), and raw transcripts never leave this device.
                """
            )
            .font(.caption)
            .foregroundStyle(.secondary)

            HStack {
                Button(summaryModel.isGenerating ? "Generating..." : "Generate AI Summary") {
                    summaryModel.generate(payload: UsageInsight.buildSummary(snapshot))
                }
                .disabled(!summaryModel.canGenerate)
                if summaryModel.isGenerating {
                    ProgressView()
                        .controlSize(.small)
                }
            }

            if !summaryModel.isCleanupEnabled {
                Text("Turn on AI cleanup to generate a summary.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            if let error = summaryModel.errorMessage {
                Text(error)
                    .foregroundStyle(.red)
            }

            if let summary = summaryModel.summary {
                Text(summary)
                    .textSelection(.enabled)
                    .padding(.top, 4)
            }
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

