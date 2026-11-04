import SwiftUI

@MainActor
final class TypingSettingsModel: ObservableObject {
    @Published private(set) var addSpaceAfterDictation: Bool
    @Published private(set) var appAwareFormatting: Bool
    @Published private(set) var defaultTextFormat: DictationTextFormat

    private let store: TypingSettingsStore
    private var observation: SettingsNotificationObservation?

    init(store: TypingSettingsStore, center: NotificationCenter = .default) {
        self.store = store
        addSpaceAfterDictation = store.addSpaceAfterDictation
        appAwareFormatting = store.appAwareFormatting
        defaultTextFormat = store.defaultTextFormat
        observation = SettingsNotificationObservation(UserDefaults.didChangeNotification, center: center) {
            [weak self] in
            self?.reload()
        }
    }

    func setAddSpaceAfterDictation(_ isOn: Bool) {
        guard isOn != addSpaceAfterDictation else { return }
        store.addSpaceAfterDictation = isOn
        reload()
    }

    func setAppAwareFormatting(_ isOn: Bool) {
        guard isOn != appAwareFormatting else { return }
        store.appAwareFormatting = isOn
        reload()
    }

    func setDefaultTextFormat(_ format: DictationTextFormat) {
        guard format != defaultTextFormat else { return }
        store.defaultTextFormat = format
        reload()
    }

    func reload() {
        let stored = store.addSpaceAfterDictation
        if stored != addSpaceAfterDictation {
            addSpaceAfterDictation = stored
        }
        let formatting = store.appAwareFormatting
        if formatting != appAwareFormatting {
            appAwareFormatting = formatting
        }
        let format = store.defaultTextFormat
        if format != defaultTextFormat {
            defaultTextFormat = format
        }
    }
}

struct InputTypingSettingsSection: View {
    @StateObject private var model: TypingSettingsModel

    init(store: TypingSettingsStore = .live) {
        _model = StateObject(wrappedValue: TypingSettingsModel(store: store))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Typing")
                .font(.headline)
            Toggle("Add a space after each dictation", isOn: addSpaceBinding)
            Text(
                "Scribe types one trailing space after the text it inserts, unless the dictation already ends in "
                    + "white space. Turn it off for an app that needs text without the trailing space. History, "
                    + "Recent Dictations and Quick Add keep the text as dictated."
            )
            .font(.footnote)
            .foregroundStyle(.secondary)
        }
        .onAppear {
            model.reload()
        }
    }

    private var addSpaceBinding: Binding<Bool> {
        Binding(get: { model.addSpaceAfterDictation }, set: { model.setAddSpaceAfterDictation($0) })
    }
}

/// App-aware formatting: Markdown pasted into the apps that understand it, and the default format for app profiles.
/// Mirrors the Windows card on Settings, Dictation, Typing. Applied at once, like the space after a dictation.
struct InputFormattingSettingsSection: View {
    @StateObject private var model: TypingSettingsModel

    init(store: TypingSettingsStore = .live) {
        _model = StateObject(wrappedValue: TypingSettingsModel(store: store))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Toggle("Use app-aware formatting", isOn: formattingBinding)
            Text(Self.description)
                .font(.footnote)
                .foregroundStyle(.secondary)
            Text("Default text format for app profiles")
                .font(.headline)
                .padding(.top, 6)
            Picker("Default text format for app profiles", selection: defaultFormatBinding) {
                ForEach(DictationTextFormat.allCases, id: \.self) { format in
                    Text(DictationFormattingText.label(format)).tag(format)
                }
            }
            .labelsHidden()
            .frame(maxWidth: 280)
            .disabled(!model.appAwareFormatting)
            Text(DictationFormattingText.sourceLimits)
                .font(.footnote)
                .foregroundStyle(.secondary)
            Text(Self.plainTextOnceNote)
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .onAppear {
            model.reload()
        }
    }

    static let description =
        "On by default. Pastes literal Markdown in known Markdown apps (such as Claude, Slack, Visual Studio Code "
        + "and Obsidian) so bullet and numbered lists don't double up. In Teams, Microsoft Copilot and Codex it "
        + "also adds a formatted copy, so lists, bold, links and code show as formatting there. Puts back what was "
        + "on your clipboard, a screenshot or formatted text included. Also lets app profiles choose a text format. "
        + "When off, those choices are ignored and every dictation is typed; existing writing styles and line-break "
        + "rules still apply. This never turns AI cleanup on."

    static let plainTextOnceNote =
        "Other apps without a matching profile use plain text, typed. Use Plain Text Once in the menu bar menu arms "
        + "the next recording for 60 seconds; it doesn't skip AI cleanup, your dictionary or snippets."

    private var formattingBinding: Binding<Bool> {
        Binding(get: { model.appAwareFormatting }, set: { model.setAppAwareFormatting($0) })
    }

    private var defaultFormatBinding: Binding<DictationTextFormat> {
        Binding(get: { model.defaultTextFormat }, set: { model.setDefaultTextFormat($0) })
    }
}
