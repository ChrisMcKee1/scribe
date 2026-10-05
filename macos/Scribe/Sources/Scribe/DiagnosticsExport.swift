import AppKit
import SwiftUI
import UniformTypeIdentifiers
import os

enum DiagnosticsExport {
    private static let exporting = OSAllocatedUnfairLock(initialState: false)

    static func create(at destination: URL, store: DiagnosticsLogStore = .live) async throws {
        let admitted = exporting.withLock { busy in
            guard !busy else { return false }
            busy = true
            return true
        }
        guard admitted else { throw CocoaError(.userCancelled) }
        defer { exporting.withLock { $0 = false } }
        let files = try await store.snapshot()
        try Task.checkCancellation()
        let scratch = FileManager.default.temporaryDirectory.appendingPathComponent(
            UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: true)
        defer {
            do { try FileManager.default.removeItem(at: scratch) } catch {
                ScribeLog.warning(.settings, "Diagnostics scratch cleanup failed", .failure(error))
            }
        }
        let report = report(logCount: files.count)
        try Data(report.utf8).write(to: scratch.appendingPathComponent("report.txt"))
        for file in files {
            try file.contents.write(to: scratch.appendingPathComponent(file.name))
        }
        // Compress only our explicit staging directory, never the data directory or Apple unified logs.
        let archive = scratch.appendingPathExtension("zip")
        defer {
            if FileManager.default.fileExists(atPath: archive.path) {
                do { try FileManager.default.removeItem(at: archive) } catch {
                    ScribeLog.warning(.settings, "Diagnostics archive cleanup failed", .failure(error))
                }
            }
        }
        let result = try await ProcessRunner.run(
            URL(fileURLWithPath: "/usr/bin/ditto"),
            arguments: ["-c", "-k", "--norsrc", scratch.path, archive.path], timeout: .seconds(30))
        guard result.succeeded else {
            throw CocoaError(.fileWriteUnknown)
        }
        try Task.checkCancellation()
        try Data(contentsOf: archive).write(to: destination, options: .atomic)
    }

    static func report(logCount: Int) -> String {
        let info = ProcessInfo.processInfo
        let version = info.operatingSystemVersion
        return """
            Scribe diagnostics
            OS: macOS \(version.majorVersion).\(version.minorVersion).\(version.patchVersion)
            Processors: \(info.processorCount)
            Memory bytes: \(info.physicalMemory)
            Log files: \(logCount)
            Logs contain static messages, numeric measurements and enum names.
            Device names, profile names and paths are redacted.
            No dictations, recordings, dictionary terms, snippets, prompts, endpoints or keys are included.
            The database, saved settings and Apple unified logs are not included.
            Retention: \(DiagnosticsLogStore.retentionDays) days, \(DiagnosticsLogStore.dayByteLimit) bytes per day,
            \(DiagnosticsLogStore.totalByteLimit) bytes total (the active day is not deleted).
            Review this archive before sharing it.

            """
    }
}

struct SaveDiagnosticsButton: View {
    @State private var saving = false
    @State private var result: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Button(saving ? "Saving diagnostics..." : "Save diagnostics...") {
                let panel = NSSavePanel()
                panel.allowedContentTypes = [.zip]
                panel.nameFieldStringValue = "Scribe-diagnostics.zip"
                guard panel.runModal() == .OK, let destination = panel.url else { return }
                saving = true
                result = nil
                Task {
                    do {
                        try await DiagnosticsExport.create(at: destination)
                        result = "Diagnostics saved. Review the archive before sharing it."
                    } catch {
                        ScribeLog.warning(.settings, "Diagnostics export failed", .failure(error))
                        result = "Could not save diagnostics. Check the destination and try again."
                    }
                    saving = false
                }
            }
            .disabled(saving)
            if let result { Text(result).cardDescription() }
        }
    }
}
