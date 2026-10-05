import Foundation

enum AiCleanupStatusKind: Sendable, Equatable {
    case none
    case info
    case success
    case warning
    case error
    case busy
}

enum AiCleanupActionID: Sendable, Equatable {
    case unload
    case checkAgain
}

struct AiCleanupAction: Sendable, Equatable {
    let id: AiCleanupActionID
    let text: String
    var isEnabled: Bool = true
}

struct AiCleanupStatusRow: Sendable, Equatable {
    let kind: AiCleanupStatusKind
    let text: String
    var primary: AiCleanupAction?
}

enum LocalAppSetup {
    static let freeMemoryAction = "Free memory"

    private static let preference = [
        ["gemma4e2b"],
        ["gemma4e4b"],
        ["qwen34binstruct", "qwen34b2507"],
        ["granite43b", "granite4micro"],
        ["gemma34b"],
        ["phi4mini"],
        ["llama323b"],
        ["qwen2515b"],
    ]

    private static let roomyGraphicsCardPreference =
        [
            preference[1],
            preference[0],
        ] + preference.dropFirst(2)

    static func pickModel(_ models: [LocalServerModel], roomyGraphicsCard: Bool = false) -> String? {
        guard !models.isEmpty else {
            return nil
        }

        for spellings in roomyGraphicsCard ? roomyGraphicsCardPreference : preference {
            for model in models {
                let key = key(for: model.id)
                if spellings.contains(where: { key.contains($0) }) {
                    return model.id
                }
            }
        }

        return models[0].id
    }

    static func modelChoices(
        _ listed: [LocalServerModel],
        _ chosen: String?,
        roomyGraphicsCard: Bool = false
    ) -> (models: [LocalServerModel], selected: String?) {
        var models = listed
        let chosenModel = chosen?.trimmingCharacters(in: .whitespacesAndNewlines).nilIfEmpty
        let selected = chosenModel ?? pickModel(listed, roomyGraphicsCard: roomyGraphicsCard)
        guard let selected else {
            return (models, nil)
        }

        if let index = models.firstIndex(where: { LocalServerClient.sameModel($0.id, selected) }) {
            if models[index].id != selected {
                let model = models[index]
                let displayName = model.displayName == model.id ? selected : model.displayName
                models[index] = LocalServerModel(
                    selected,
                    displayName,
                    model.sizeBytes,
                    maxContextTokens: model.maxContextTokens)
            }
        } else {
            models.insert(LocalServerModel(selected, selected, 0), at: 0)
        }

        return (models, selected)
    }

    static func describe(
        _ app: LocalServerApp,
        _ state: LocalServerState?,
        _ model: String?,
        idleMinutes: Int
    ) -> AiCleanupStatusRow {
        let name = app.displayName
        let checkAgain = AiCleanupAction(id: .checkAgain, text: "Check again")
        guard let state else {
            return AiCleanupStatusRow(kind: .busy, text: "Checking \(name)...", primary: nil)
        }

        switch state.reach {
        case .notRunning:
            return AiCleanupStatusRow(
                kind: .warning,
                text: "Scribe can't reach \(name). Open \(name), then choose Check again.",
                primary: checkAgain)
        case .failed:
            return AiCleanupStatusRow(
                kind: .warning,
                text: "\(name) didn't answer. Make sure it's open and up to date, then choose Check again.",
                primary: checkAgain)
        case .needsKey:
            return AiCleanupStatusRow(
                kind: .warning,
                text: "\(name) asks for an API key. To use one, choose Another AI service and enter the key there.",
                primary: checkAgain)
        case .reached:
            break
        }

        if state.models.isEmpty {
            return AiCleanupStatusRow(
                kind: .info,
                text: "\(name) has no models yet. Download one in \(name), then choose Check again.",
                primary: checkAgain)
        }

        guard let model = model?.trimmingCharacters(in: .whitespacesAndNewlines).nilIfEmpty else {
            return AiCleanupStatusRow(kind: .info, text: "Choose a model.", primary: nil)
        }

        if let loaded = state.loaded(for: model) {
            let memory = loaded.memoryBytes > 0 ? "\(formatSize(loaded.memoryBytes)) of memory" : "memory"
            let freed: String
            if idleMinutes > 0 {
                let unit = idleMinutes == 1 ? "minute" : "minutes"
                freed = " Scribe asks \(name) to free it after \(idleMinutes) \(unit) without a dictation."
            } else {
                freed = ""
            }
            return AiCleanupStatusRow(
                kind: .success,
                text: "\(model) is using \(memory).\(freed) \(sharedModelNote(name))",
                primary: AiCleanupAction(id: .unload, text: freeMemoryAction))
        }

        if !state.models.contains(where: { LocalServerClient.sameModel($0.id, model) }) {
            return AiCleanupStatusRow(
                kind: .warning,
                text: "\(name) doesn't list \(model). Choose another model, or download it in \(name).",
                primary: checkAgain)
        }

        return AiCleanupStatusRow(
            kind: .info,
            text: "\(model) isn't using memory now. It loads when you dictate.",
            primary: nil)
    }

    static func sharedModelNote(_ appName: String) -> String {
        "Free memory unloads it from \(appName), for other apps too."
    }

    static func formatSize(_ bytes: Int64) -> String {
        let gigabyte = 1024.0 * 1024.0 * 1024.0
        let megabyte = 1024.0 * 1024.0
        let value = Double(bytes)
        if value >= gigabyte {
            return String(format: "%.1f GB", value / gigabyte)
        }
        return String(format: "%.0f MB", max(1.0, round(value / megabyte)))
    }

    private static func key(for name: String) -> String {
        String(name.lowercased().filter { $0.isLetter || $0.isNumber })
    }
}

extension String {
    fileprivate var nilIfEmpty: String? {
        isEmpty ? nil : self
    }
}
