import SwiftUI

@MainActor
final class TypingSettingsModel: ObservableObject {
    @Published private(set) var addSpaceAfterDictation: Bool

    private let store: TypingSettingsStore
    private var observation: SettingsNotificationObservation?

    init(store: TypingSettingsStore, center: NotificationCenter = .default) {
        self.store = store
        addSpaceAfterDictation = store.addSpaceAfterDictation
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

    func reload() {
        let stored = store.addSpaceAfterDictation
        if stored != addSpaceAfterDictation {
            addSpaceAfterDictation = stored
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
