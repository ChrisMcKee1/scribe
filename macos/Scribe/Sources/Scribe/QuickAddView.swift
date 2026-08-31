import SwiftUI

/// The tray's quick "Add to dictionary" popup: pick a recent dictation, tap the word(s) the
/// recognizer got wrong, type the correction, save. All of the decisions (what counts as a chip,
/// what a selection turns into, whether the typed rule creates/updates/no-ops) live in
/// `QuickDictionaryAdd`; this view is only the surface, mirroring Windows' `QuickAddWindow`.
///
/// Simplified relative to Windows on purpose: chip selection here is tap-to-extend and
/// tap-to-shrink only (no drag-select), since the AppleScript UI automation this project verifies
/// against cannot exercise a drag gesture anyway, and a single tap-based gesture already covers the
/// "join two words" and "pick one word" cases that motivate the feature.
struct QuickAddView: View {
    /// What a successful save produced, mirroring Windows' `QuickAddResult`: the entry now in the
    /// database, the transcript it was built from, and that transcript rewritten by the new rule
    /// (nil when the rule did not change this particular transcript).
    struct SavedResult {
        let entry: DictionaryEntry
        let sourceTranscript: String?
        let correctedTranscript: String?
    }

    let recentTranscripts: [String]
    let existing: [DictionaryEntry]
    let onSave: (SavedResult) -> Void
    let onClose: () -> Void

    @State private var selectedTranscriptIndex = 0
    @State private var selection: QuickDictionaryAdd.WordRange = .none
    @State private var heard = ""
    @State private var written = ""
    @State private var wholeWord = true
    @State private var errorMessage: String?

    private var transcript: String {
        recentTranscripts.indices.contains(selectedTranscriptIndex) ? recentTranscripts[selectedTranscriptIndex] : ""
    }

    private var tokens: [QuickDictionaryAdd.Token] {
        QuickDictionaryAdd.tokenize(transcript)
    }

    private var plan: QuickDictionaryAdd.Plan {
        QuickDictionaryAdd.build(pattern: heard, replacement: written, wholeWord: wholeWord, existing: existing)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Add to Dictionary")
                .font(.headline)

            if recentTranscripts.isEmpty {
                Text("No recent dictations to pick a word from yet. Dictate something first, or type the spoken form directly below.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            } else {
                Picker("Recent dictation", selection: $selectedTranscriptIndex) {
                    ForEach(recentTranscripts.indices, id: \.self) { index in
                        Text(LastTranscriptStore.formatPreview(recentTranscripts[index])).tag(index)
                    }
                }
                .labelsHidden()
                .onChange(of: selectedTranscriptIndex) { _ in
                    selection = .none
                    heard = ""
                }

                Text("Tap the word Scribe got wrong. Tap an adjacent chip to extend the phrase.")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                ChipFlowLayout(spacing: 6) {
                    ForEach(Array(tokens.enumerated()), id: \.offset) { index, token in
                        Button(token.text) {
                            selection = QuickDictionaryAdd.toggle(selection, index: index)
                            heard = QuickDictionaryAdd.select(transcript, tokens: tokens, first: selection.first, last: selection.last)
                        }
                        .buttonStyle(.plain)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 4)
                        .background(isSelected(index) ? Color.accentColor : Color.gray.opacity(0.2))
                        .foregroundStyle(isSelected(index) ? Color.white : Color.primary)
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                        // Custom-styled buttons like this one otherwise report no accessible
                        // title at all (verified with System Events: `title`/`name` both came
                        // back missing), leaving VoiceOver with nothing to announce for a chip.
                        .accessibilityLabel(isSelected(index) ? "\(token.text), selected" : token.text)
                        .accessibilityAddTraits(isSelected(index) ? [.isButton, .isSelected] : .isButton)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            Divider()

            TextField("Heard (what Scribe wrote)", text: $heard)
            TextField("Should be", text: $written)
            Toggle("Whole word only", isOn: $wholeWord)

            Text(plan.message)
                .font(.caption)
                .foregroundStyle(plan.kind == .invalid ? .red : .secondary)

            HStack {
                Spacer()
                Button("Cancel", action: onClose)
                    .accessibilityLabel("Cancel")
                Button(saveButtonTitle) { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!plan.canSave)
                    .accessibilityLabel(saveButtonTitle)
            }

            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }
        }
        .padding(16)
        .frame(minWidth: 420, idealWidth: 460)
    }

    private var saveButtonTitle: String {
        plan.kind == .update ? "Update Rule" : "Save"
    }

    private func isSelected(_ index: Int) -> Bool {
        !selection.isEmpty && index >= selection.first && index <= selection.last
    }

    private func save() {
        guard let entry = plan.entry else { return }

        let sourceTranscript = transcript.isEmpty ? nil : transcript
        do {
            let savedID = try persist(entry)
            let saved = DictionaryEntry(
                id: savedID,
                pattern: entry.pattern,
                replacement: entry.replacement,
                wholeWord: entry.wholeWord,
                enabled: entry.enabled)
            let corrected = sourceTranscript.map { QuickDictionaryAdd.apply($0, entry: saved) }
            onSave(SavedResult(
                entry: saved,
                sourceTranscript: sourceTranscript,
                correctedTranscript: (corrected != sourceTranscript) ? corrected : nil))
        } catch {
            errorMessage = "Couldn't save that rule: \(error.localizedDescription)"
        }
    }

    /// Persisting is injected via `persistAction` in production so a `.invalid`/`.noChange` plan
    /// (which carries no writable entry) never reaches here; `save()` already guards on
    /// `plan.entry`. Exposed as a var so previews/tests can stub it without a real database.
    var persistAction: ((DictionaryEntry) throws -> Int64)?

    private func persist(_ entry: DictionaryEntry) throws -> Int64 {
        guard let persistAction else {
            throw QuickAddPersistError.noPersistAction
        }
        return try persistAction(entry)
    }
}

enum QuickAddPersistError: LocalizedError {
    case noPersistAction

    var errorDescription: String? {
        "No persistence action configured."
    }
}

/// Wraps word-chip buttons onto as many rows as needed, since `HStack` never wraps and a long
/// dictation would otherwise run off the edge of the popup. Uses SwiftUI's `Layout` protocol
/// (macOS 13+, matching this package's deployment target) rather than a third-party flow-layout
/// dependency, since the wrapping rule needed here (left-to-right, wrap at the container width) is
/// exactly what `Layout` is for.
struct ChipFlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let maxWidth = proposal.width ?? .infinity
        var rowWidth: CGFloat = 0
        var totalHeight: CGFloat = 0
        var rowHeight: CGFloat = 0

        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if rowWidth + size.width > maxWidth, rowWidth > 0 {
                totalHeight += rowHeight + spacing
                rowWidth = 0
                rowHeight = 0
            }
            rowWidth += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
        totalHeight += rowHeight
        return CGSize(width: maxWidth.isFinite ? maxWidth : rowWidth, height: totalHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var origin = bounds.origin
        var rowHeight: CGFloat = 0

        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if origin.x + size.width > bounds.maxX, origin.x > bounds.origin.x {
                origin.x = bounds.origin.x
                origin.y += rowHeight + spacing
                rowHeight = 0
            }
            subview.place(at: origin, proposal: ProposedViewSize(size))
            origin.x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
    }
}
