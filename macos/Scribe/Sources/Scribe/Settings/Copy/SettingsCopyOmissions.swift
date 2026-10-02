import Foundation

/// A Windows string the Mac does not carry over, and why.
struct CopyOmission: Equatable, Sendable {
    let windows: String
    let because: CopyDeviation
}

/// The Windows strings that have no Mac twin, so a reader of the catalogs can see what was left out on purpose.
/// Each string is held to the Windows sources by `CopyManifestTests`, so one that Windows rewrites fails there.
enum SettingsCopyOmissions {
    static let all: [CopyOmission] = [
        CopyOmission(windows: "Restart to update Scribe", because: .windowsOnly),
        CopyOmission(windows: "Check for updates", because: .windowsOnly),
        CopyOmission(windows: "Restart to update", because: .windowsOnly),
        CopyOmission(windows: "Scribe does not look for updates until you ask.", because: .windowsOnly),
        CopyOmission(windows: "Rate Scribe in the Microsoft Store", because: .windowsOnly),
        CopyOmission(windows: "Ratings and reviews are handled by the Microsoft Store.", because: .windowsOnly),
        CopyOmission(windows: "Share Scribe", because: .windowsOnly),
        CopyOmission(windows: "Use my Windows accent color", because: .windowsOnly),
        CopyOmission(windows: "Save diagnostics...", because: .macBehaviour),
        CopyOmission(windows: "Logs folder", because: .macBehaviour),
        CopyOmission(
            windows: "Uses your GitHub Copilot subscription. Your text goes to GitHub.",
            because: .copilotUnavailable),
        CopyOmission(
            windows: "Scribe never asks for or stores a GitHub token.",
            because: .copilotUnavailable),
        CopyOmission(
            windows: "Remote Desktop and virtual machine windows are always typed into",
            because: .windowsOnly),
        CopyOmission(windows: "Windows startup settings", because: .macSystemFeature),
        CopyOmission(windows: "Save a recording with each dictation", because: .macBehaviour),
        CopyOmission(windows: "Free memory when Scribe isn't used", because: .macBehaviour),
        CopyOmission(windows: "After this long without a dictation, Scribe frees the memory", because: .macBehaviour),
        CopyOmission(windows: "After 10 minutes (default)", because: .macBehaviour),
        CopyOmission(windows: "Report an AI result...", because: .macBehaviour),
        CopyOmission(windows: "Report an AI cleanup problem...", because: .macBehaviour),
        CopyOmission(windows: "Scribe sends nothing by itself.", because: .macBehaviour),
        CopyOmission(
            windows: "Keeps the audio of each dictation on this PC for up to 7 days (250 MB in total)",
            because: .macBehaviour),
    ]
}
