import Charts
import SwiftUI

struct SettingsUsagePage: View {
    let persistenceStore: PersistenceStore
    let onChanged: @MainActor () -> Void

    var body: some View {
        SettingsPage(title: "Usage", subtitle: "How much you've dictated, and words you might add to your dictionary.")
        {
            UsageInsightsSettingsTab(persistenceStore: persistenceStore, onChanged: onChanged)
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
    @State private var refreshID = UUID()

    init(persistenceStore: PersistenceStore, onChanged: @escaping @MainActor () -> Void) {
        _model = StateObject(wrappedValue: UsageInsightsModel(access: .live(persistenceStore), onChanged: onChanged))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .bottom) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Usage")
                        .font(.headline)
                    Text("Period")
                        .cardDescription()
                }
                Spacer()
                Picker("Period", selection: $model.windowDays) {
                    Text("7 days").tag(7.0)
                    Text("30 days").tag(30.0)
                    Text("90 days").tag(90.0)
                }
                .pickerStyle(.segmented)
                .frame(width: 260)
                .onChange(of: model.windowDays) { _ in
                    summaryModel.reset()
                    refreshID = UUID()
                }
                Button("Refresh") {
                    summaryModel.reset()
                    refreshID = UUID()
                }
            }
            .id("usage.period")

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
                        SettingsCard(searchID: "usage.totals") { totalsSection(snapshot) }
                        LazyVGrid(
                            columns: [
                                GridItem(.flexible(), alignment: .top),
                                GridItem(.flexible(), alignment: .top),
                            ],
                            alignment: .leading,
                            spacing: 12
                        ) {
                            SettingsCard { topAppsSection(snapshot) }
                            SettingsCard { trendSection(snapshot) }
                            SettingsCard { knownTermsSection(snapshot) }
                            SettingsCard(searchID: "usage.terms") { termsSection(snapshot) }
                        }
                        SettingsCard(searchID: "usage.summary") { aiSummarySection(snapshot) }
                    }
                }
            } else {
                Text(
                    model.load.state == .loading || model.load.state == .unloaded
                        ? "Reading usage for the selected period..."
                        : model.load.state == .failed
                            ? "Usage could not be read. Choose Refresh to try again."
                            : "No dictations in this window yet."
                )
                .foregroundStyle(.secondary)
            }

            Spacer()
        }
        .task(id: refreshID) {
            await model.reload()
        }
        .onDisappear {
            summaryModel.cancelInFlight()
        }
    }

    // MARK: Totals

    private func totalsSection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Totals")
                .cardTitle()
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), alignment: .leading), count: 5), spacing: 12) {
                metricTile(value: "\(snapshot.dictations)", label: "dictations")
                metricTile(value: "\(snapshot.words)", label: "words")
                metricTile(value: "\(snapshot.activeDays)", label: "active days")
                metricTile(value: String(format: "%.1f min", snapshot.speechSeconds / 60.0), label: "speaking time")
                metricTile(value: String(format: "%.1f", snapshot.averageWords), label: "words per dictation")
            }
        }
    }

    // MARK: Trend

    private func trendSection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(snapshot.granularity == .daily ? "Trend (daily)" : "Trend (weekly)")
                .cardTitle()
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
                .chartXAxis(.hidden)
            }
        }
    }

    // MARK: Top apps

    private func topAppsSection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Top apps")
                .cardTitle()
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
            Text("Words you could add")
                .cardTitle()
            Text("Words that came up often and aren't in your dictionary yet.")
                .cardDescription()
            let novelTerms = snapshot.terms.filter { !$0.covered }
            if novelTerms.isEmpty {
                Text("No new words in this period.")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(novelTerms, id: \.text) { term in
                    HStack {
                        Text(term.text)
                        Text("(\(term.dictations) dictations, \(term.occurrences)x)")
                            .foregroundStyle(.secondary)
                            .font(.caption)
                        Spacer()
                        Button("Add to dictionary") {
                            Task { await model.addTermToDictionary(term) }
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
                .cardTitle()
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
                Button(summaryModel.isGenerating ? "Generating..." : "Get summary") {
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

    private func knownTermsSection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Words your dictionary already knows")
                .cardTitle()
            Text("Dictionary and word pack words that came up in this period.")
                .cardDescription()
            let covered = snapshot.terms.filter(\.covered)
            if covered.isEmpty {
                Text("No known words in this period.")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(covered, id: \.text) { term in
                    HStack {
                        Text(term.text)
                        Spacer()
                        Text("\(term.dictations) dictations")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }
        }
    }

    private func metricTile(value: String, label: String) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(value)
                .font(.title3.weight(.semibold))
                .monospacedDigit()
            Text(label)
                .cardDescription()
        }
    }
}
