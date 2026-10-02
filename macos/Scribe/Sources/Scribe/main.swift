import AppKit

// The entry point only: a command-line verb runs and exits (`CommandLineTools.swift`); otherwise the menu bar app
// starts (`AppDelegate.swift`).
if CommandLineTranscriptionTool.runIfRequested() {
    exit(EXIT_SUCCESS)
}

let application = NSApplication.shared
let diagnosticsObservation = ScribeLog.addObserver { DiagnosticsLogStore.live.append($0) }
atexit {
    ScribeLog.info(.app, "Session ended", .integer("pid", ProcessInfo.processInfo.processIdentifier))
    DiagnosticsLogStore.live.finish()
}
ScribeLog.info(
    .app, "Session started", .integer("pid", ProcessInfo.processInfo.processIdentifier),
    .count("processors", ProcessInfo.processInfo.processorCount),
    .integer("memoryBytes", ProcessInfo.processInfo.physicalMemory))
let delegate = AppDelegate()
application.setActivationPolicy(.accessory)
application.delegate = delegate
application.run()
