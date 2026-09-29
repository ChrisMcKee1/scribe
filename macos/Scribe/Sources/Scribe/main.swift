import AppKit

// The entry point only: a command-line verb runs and exits (`CommandLineTools.swift`); otherwise the menu bar app
// starts (`AppDelegate.swift`).
if CommandLineTranscriptionTool.runIfRequested() {
    exit(EXIT_SUCCESS)
}

let application = NSApplication.shared
let delegate = AppDelegate()
application.setActivationPolicy(.accessory)
application.delegate = delegate
application.run()
