import SwiftUI

struct SettingsAdvancedPage: View {
    private let newlineStore: AdvancedDictationSettingsStore
    @State private var newlineMode: NewlineInjectionMode

    init(newlineStore: AdvancedDictationSettingsStore = .live) {
        self.newlineStore = newlineStore
        _newlineMode = State(initialValue: newlineStore.newlineMode)
    }

    var body: some View {
        SettingsPage(
            title: "Advanced",
            subtitle: "Settings most people never need to change. The defaults suit most Macs."
        ) {
            VStack(alignment: .leading, spacing: 14) {
                SettingsGroupHeader("Speech recognition")
                SettingsCard { speechModelCard }
                SettingsCard {
                    readOnlyCard(
                        title: "Processor threads",
                        value: "Automatic",
                        description:
                            "Scribe does not pass a processor-thread setting to Foundry Local. The foundry transcribe command chooses how to run on this Mac."
                    )
                }
                SettingsCard {
                    readOnlyCard(
                        title: "Free memory when Scribe is not used",
                        value: "Managed by the recognizer",
                        description:
                            "Scribe starts a recognizer subprocess for each dictation and keeps only a warm status for timing. Foundry Local manages its own model cache."
                    )
                }
                Text("Changes to the recognizer backend take effect on the next dictation.")
                    .cardDescription()

                SettingsGroupHeader("Recording")
                SettingsCard {
                    readOnlyCard(
                        title: "Trim silence",
                        value: "No separate trim step",
                        description:
                            "macOS sends the captured recording to the recognizer as recorded. Silence auto-stop can end toggle and test dictations, but there is no separate silence trimming stage."
                    )
                }
                SettingsCard {
                    readOnlyCard(
                        title: "Longest recording",
                        value: "10 minutes",
                        description:
                            "A recording stops at ten minutes and Scribe types what it heard, so a stuck key cannot record forever."
                    )
                }

                SettingsGroupHeader("Typing into apps")
                SettingsCard {
                    readOnlyCard(
                        title: "Typing method",
                        value: "Accessibility insertion, then paste, then typing",
                        description:
                            "Scribe first writes through the macOS Accessibility API. If the focused element does not accept that, it borrows the pasteboard for a Command-V paste and restores it when it can. If the paste path is not safe, it types Unicode keystrokes."
                    )
                }
                SettingsCard { lineBreaksCard }
                SettingsCard {
                    readOnlyCard(
                        title: "Do not send chat messages early",
                        value: "On for typed fallback line breaks",
                        description:
                            "When Scribe has to type line breaks as keystrokes, it uses Shift-Return so chat apps such as Teams and Slack start a new line instead of sending."
                    )
                }

                SettingsGroupHeader("Text changes")
                SettingsCard {
                    readOnlyCard(
                        title: "Apply your dictionary and snippets",
                        value: "On",
                        description:
                            "Every dictation runs through your dictionary, snippets and spacing fixes. When AI cleanup is on, Scribe applies vocabulary before the request and finishes snippets and template-style replacements after the reply."
                    )
                }
            }
        }
        .onAppear { newlineMode = newlineStore.newlineMode }
    }

    private var speechModelCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Speech model").cardTitle()
            valuePill("Foundry Local parakeet-tdt-0.6b-v2")
            Text(
                "macOS uses Foundry Local as the production recognizer and passes parakeet-tdt-0.6b-v2 to foundry transcribe. If SCRIBE_WHISPER_CLI and SCRIBE_WHISPER_MODEL are set, the developer fallback is whisper.cpp with ggml-tiny.en."
            )
            .cardDescription()
        }
    }

    private var lineBreaksCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Line breaks").cardTitle()
            Text("In command-line apps such as Terminal, a line break works like Return and can send text early.")
                .cardDescription()
            Picker(
                "Line breaks",
                selection: Binding(
                    get: { newlineMode },
                    set: { mode in
                        newlineMode = mode
                        newlineStore.newlineMode = mode
                    })
            ) {
                Text("Smart flatten for terminals").tag(NewlineInjectionMode.smartFlatten)
                Text("Always flatten to spaces").tag(NewlineInjectionMode.alwaysFlatten)
                Text("Keep line breaks").tag(NewlineInjectionMode.keepNewlines)
            }
            .pickerStyle(.menu)
            .frame(maxWidth: 420, alignment: .leading)
            Text(description(for: newlineMode))
                .cardDescription()
        }
    }

    private func readOnlyCard(title: String, value: String, description: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).cardTitle()
            valuePill(value)
            Text(description).cardDescription()
        }
    }

    private func valuePill(_ value: String) -> some View {
        Text(value)
            .font(.callout.weight(.semibold))
            .padding(.horizontal, 10)
            .padding(.vertical, 5)
            .background(
                Capsule(style: .continuous)
                    .fill(Color.accentColor.opacity(0.14)))
    }

    private func description(for mode: NewlineInjectionMode) -> String {
        switch mode {
        case .smartFlatten:
            return "Scribe keeps paragraphs in editors and flattens line breaks only for known terminal apps."
        case .alwaysFlatten:
            return "Scribe replaces every line break with a space before insertion."
        case .keepNewlines:
            return "Scribe keeps the line breaks produced by cleanup and snippets."
        }
    }
}
