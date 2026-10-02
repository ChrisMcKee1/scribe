import Foundation
import os

enum SettingsIntentRevision {
    private static let counter = OSAllocatedUnfairLock<UInt64>(initialState: 0)

    static func next() -> UInt64 {
        counter.withLock {
            $0 += 1
            return $0
        }
    }
}

/// Port of Windows' ExternalChoiceSync: request and completion carry the same event-time revision.
struct SettingsExternalChoice<Value: Equatable & Sendable>: Sendable {
    private(set) var newestRevision: UInt64 = 0
    private var waiting: Value?

    var hasWaitingChange: Bool { waiting != nil }

    mutating func adopt(_ value: Value, revision: UInt64, canShowNow: Bool) -> Bool {
        guard revision >= newestRevision else { return false }
        newestRevision = revision
        waiting = canShowNow ? nil : value
        return true
    }

    mutating func release() -> Value? {
        defer { waiting = nil }
        return waiting
    }

    mutating func userChanged(revision: UInt64 = SettingsIntentRevision.next()) {
        waiting = nil
        newestRevision = revision
    }

    mutating func savedThrough(_ revision: UInt64) {
        if newestRevision <= revision {
            newestRevision = 0
        }
    }

    mutating func saved() {
        newestRevision = 0
    }

    func forSave(_ shown: Value) -> Value {
        waiting ?? shown
    }
}

typealias SettingsExternalSwitch = SettingsExternalChoice<Bool>
