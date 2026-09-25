import Foundation

/// Everything a successful Clear history empties, so none of the deleted text stays on screen or can be brought back:
/// the Recent Dictations ring and its submenu if it is open (beyond Windows, whose tray list survives a Clear), the
/// transcripts earlier notifications would copy, a pill notice that offers one, the Playground's last report, and an
/// open Quick Add window, whose picker holds its own copy of the ring.
///
/// A dictation still being processed when the Clear ran is not fenced out: it records afterwards, in history, the
/// ring and the Playground, because its text was not part of what the Clear deleted.
@MainActor
struct HistoryClearedEffects {
    let recovery: LastTranscriptStore
    let reports: PipelineReportStore
    /// The transcripts notifications already posted would copy (`DictationNotificationCenter.forgetRecoveryTexts`).
    let forgetNotificationTexts: @MainActor () -> Void
    /// A pill notice that offers the text (`DictationController.recoveryWasCleared`).
    let withdrawPillRecovery: @MainActor () -> Void
    /// The entries of the Recent Dictations submenu, if it is open (`RecentDictationsMenu.invalidate`).
    let invalidateRecentDictationsMenu: @MainActor () -> Void
    /// Closes the Quick Add window; opening it again reads the emptied ring.
    let closeQuickAdd: @MainActor () -> Void

    func apply() {
        recovery.removeAll()
        reports.clear()
        forgetNotificationTexts()
        withdrawPillRecovery()
        invalidateRecentDictationsMenu()
        closeQuickAdd()
    }
}
