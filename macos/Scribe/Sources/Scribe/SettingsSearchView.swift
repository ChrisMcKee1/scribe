import AppKit
import SwiftUI

struct SettingsSearchActivation: Equatable {
    let result: SettingsSearchResult
    let token: UUID
}

private struct SettingsSearchHighlightKey: EnvironmentKey {
    static let defaultValue: String? = nil
}

extension EnvironmentValues {
    var settingsSearchHighlightID: String? {
        get { self[SettingsSearchHighlightKey.self] }
        set { self[SettingsSearchHighlightKey.self] = newValue }
    }
}

struct SettingsSearchSidebarHeader: View {
    @Binding var query: String
    @Binding var selectedIndex: Int
    let isShowingResults: Bool
    let results: [SettingsSearchResult]
    let onActivate: (SettingsSearchResult) -> Void
    let onShowResults: () -> Void
    let onClear: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SettingsSearchField(
                text: $query,
                onMoveUp: moveSelectionUp,
                onMoveDown: moveSelectionDown,
                onCommit: activateSelection,
                onEscape: onClear,
                onBeginEditing: onShowResults
            )
            .frame(height: 28)
            .accessibilityLabel("Find a setting")

            if isShowingResults && !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                searchResults
            }
        }
        .padding(.horizontal, 10)
        .padding(.top, 10)
        .padding(.bottom, isShowingResults ? 8 : 6)
    }

    @ViewBuilder
    private var searchResults: some View {
        if results.isEmpty {
            Text("No settings found")
                .font(.caption)
                .foregroundStyle(.secondary)
                .padding(.horizontal, 8)
                .padding(.vertical, 7)
                .frame(maxWidth: .infinity, alignment: .leading)
                .accessibilityLabel("No settings found")
        } else {
            VStack(alignment: .leading, spacing: 3) {
                ForEach(Array(results.enumerated()), id: \.element.entry.id) { index, result in
                    Button {
                        selectedIndex = index
                        onActivate(result)
                    } label: {
                        Text(result.displayText)
                            .font(.caption)
                            .lineLimit(2)
                            .multilineTextAlignment(.leading)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.horizontal, 8)
                            .padding(.vertical, 6)
                            .background(
                                RoundedRectangle(cornerRadius: 6, style: .continuous)
                                    .fill(index == selectedIndex ? Color.accentColor.opacity(0.18) : Color.clear)
                            )
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(result.displayText)
                }
            }
        }
    }

    private func moveSelectionUp() {
        guard !results.isEmpty else { return }
        selectedIndex = selectedIndex <= 0 ? results.count - 1 : selectedIndex - 1
    }

    private func moveSelectionDown() {
        guard !results.isEmpty else { return }
        selectedIndex = selectedIndex >= results.count - 1 ? 0 : selectedIndex + 1
    }

    private func activateSelection() {
        guard results.indices.contains(selectedIndex) else { return }
        onActivate(results[selectedIndex])
    }
}

struct SettingsSearchField: NSViewRepresentable {
    @Binding var text: String
    let onMoveUp: () -> Void
    let onMoveDown: () -> Void
    let onCommit: () -> Void
    let onEscape: () -> Void
    let onBeginEditing: () -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(self)
    }

    func makeNSView(context: Context) -> SearchFieldView {
        let view = SearchFieldView()
        view.placeholderString = "Find a setting"
        view.bezelStyle = .roundedBezel
        view.sendsWholeSearchString = false
        view.sendsSearchStringImmediately = true
        view.delegate = context.coordinator
        view.onMoveUp = onMoveUp
        view.onMoveDown = onMoveDown
        view.onCommit = onCommit
        view.onEscape = onEscape
        view.onBeginEditing = onBeginEditing
        view.setAccessibilityLabel("Find a setting")
        return view
    }

    func updateNSView(_ nsView: SearchFieldView, context: Context) {
        context.coordinator.parent = self
        nsView.onMoveUp = onMoveUp
        nsView.onMoveDown = onMoveDown
        nsView.onCommit = onCommit
        nsView.onEscape = onEscape
        nsView.onBeginEditing = onBeginEditing
        if nsView.stringValue != text {
            nsView.stringValue = text
        }
    }

    final class Coordinator: NSObject, NSSearchFieldDelegate {
        var parent: SettingsSearchField

        init(_ parent: SettingsSearchField) {
            self.parent = parent
        }

        func controlTextDidChange(_ notification: Notification) {
            guard let field = notification.object as? NSSearchField else { return }
            parent.text = field.stringValue
            parent.onBeginEditing()
        }

        func controlTextDidBeginEditing(_ notification: Notification) {
            parent.onBeginEditing()
        }
    }
}

final class SearchFieldView: NSSearchField {
    var onMoveUp: () -> Void = {}
    var onMoveDown: () -> Void = {}
    var onCommit: () -> Void = {}
    var onEscape: () -> Void = {}
    var onBeginEditing: () -> Void = {}

    override func becomeFirstResponder() -> Bool {
        let became = super.becomeFirstResponder()
        if became {
            onBeginEditing()
        }
        return became
    }

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 125:
            onMoveDown()
        case 126:
            onMoveUp()
        case 36, 76:
            onCommit()
        case 53:
            onEscape()
        default:
            super.keyDown(with: event)
        }
    }
}
