import SwiftUI

struct DictionaryWordEditorView: View {
    let existing: [DictionaryEntry]
    let title: String
    let onSave: @MainActor (_ forms: [String], _ replacement: String) async -> Bool
    let onCancel: () -> Void

    @State private var replacement: String
    @State private var forms: [String]
    @State private var errorMessage: String?
    @State private var isSaving = false

    init(
        existing: [DictionaryEntry],
        title: String = "Add word",
        initialReplacement: String = "",
        initialForms: [String] = [""],
        onSave: @escaping @MainActor (_ forms: [String], _ replacement: String) async -> Bool,
        onCancel: @escaping () -> Void
    ) {
        self.existing = existing
        self.title = title
        self.onSave = onSave
        self.onCancel = onCancel
        _replacement = State(initialValue: initialReplacement)
        _forms = State(initialValue: initialForms.isEmpty ? [""] : initialForms)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title)
                .font(.headline)

            Text("Scribe writes")
                .font(.subheadline.weight(.semibold))
            TextField("Written form", text: $replacement)

            Text("Scribe hears")
                .font(.subheadline.weight(.semibold))
            Text("Each box is one way. Spaces and commas stay part of that way.")
                .font(.caption)
                .foregroundStyle(.secondary)

            ForEach(forms.indices, id: \.self) { index in
                HStack {
                    TextField(index == 0 ? "Heard form" : "Another way Scribe hears it", text: binding(for: index))
                    if index > 0 {
                        Button(role: .destructive) {
                            forms.remove(at: index)
                        } label: {
                            Image(systemName: "minus.circle")
                        }
                        .buttonStyle(.plain)
                    }
                }
            }

            Button("Add another way") {
                forms.append("")
            }

            Text(message)
                .font(.caption)
                .foregroundStyle(result.succeeded ? Color.secondary : .red)

            HStack {
                Spacer()
                Button("Cancel", action: onCancel)
                Button("Save") {
                    save()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!result.canSave || isSaving)
            }

            if let errorMessage {
                Text(errorMessage)
                    .font(.caption)
                    .foregroundStyle(.red)
            }
        }
        .padding(16)
        .frame(minWidth: 420)
    }

    private var result: DictionaryWordEditor.Result {
        DictionaryWordEditor.build(existing: existing, editedIndex: nil, replacement: replacement, forms: forms)
    }

    private var message: String {
        if let error = result.error {
            return error
        }
        let count = result.addedEntries.count + (result.editedEntry == nil ? 0 : 1)
        if count == 0 {
            return DictionaryWordEditor.spokenEmptyMessage
        }
        return count == 1
            ? "Scribe will add this way to your dictionary when you save."
            : "Scribe will add these ways to your dictionary when you save."
    }

    private func binding(for index: Int) -> Binding<String> {
        Binding(
            get: { forms[index] },
            set: { forms[index] = $0 })
    }

    private func save() {
        guard result.canSave, !isSaving else { return }
        isSaving = true
        errorMessage = nil
        Task {
            let succeeded = await onSave(forms, replacement)
            isSaving = false
            if succeeded {
                onCancel()
            } else if let error = result.error {
                errorMessage = error
            }
        }
    }
}
