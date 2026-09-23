# Scribe 0.4.3

This release is about the things you notice without looking for them. Start with Windows now
takes effect the moment you flip it. Scribe's database stops growing without bound, and an older
one shrinks: in our tests a heavy history went from 474 MB to 37 MB. On-device AI files you no
longer use are cleaned up for you. And a long list of fixes makes dictation, pausing and quitting
behave the same way every time.

## Reliability and privacy

- Pausing dictation now lets your push-to-talk key through, so it works normally in other apps
  until you resume.
- If you copy something while a dictation is being inserted, Scribe keeps it. Scribe puts your
  previous clipboard text back only when the clipboard still holds exactly what Scribe placed there,
  and if another app copies just before Scribe pastes, Scribe types the dictation instead of
  pasting the other app's text.
- Long dictations no longer lose words where Scribe splits them into 30 second pieces. The split
  points now land on pauses in your speech.
- Silence auto-stop adapts to your microphone and your room. Quiet microphones are heard, and a
  noisy room no longer keeps a recording open forever. It also follows the hotkey you actually
  pressed, so a held key never auto-stops in the middle of a sentence.
- Pausing, or a microphone problem, right as a dictation starts no longer leaves the microphone on.
- Scribe's logs no longer record what you dictated, the address of a custom AI endpoint, Azure
  resource names, dictionary or snippet text, or error text returned by an AI service. Log files
  that earlier versions wrote are cleaned of those values once, in place, in the formats those
  versions used, and the zip that Save diagnostics creates is cleaned too, with a summary of what
  was replaced.
- If Scribe cannot start, it now says so and closes, instead of staying invisible in the background
  and blocking the next launch. When its data was created by a newer version, it tells you to
  install the latest version.
- If Scribe has to repair a damaged database and your settings cannot be recovered, it keeps all of
  your history text until you review and save your settings, and it tells you what it could and
  could not recover.
- Quitting in the middle of a dictation, or while Scribe is tidying its storage, finishes cleanly
  and in a bounded time.

## Performance and storage

- Stored recordings are kept for 7 days and 250 MB at most, oldest first, and take about half the
  space they used to. Their text stays in your history, which follows your retention setting.
- An older, larger database is compacted once in the background, only while you are not dictating
  and Settings is closed, and it stops the moment you start dictating.
- The next dictation can start as soon as your text is inserted; saving history happens in the
  background.
- Samples of failed AI cleanups are removed after seven days, whether or not later cleanups succeed.
- The files behind the recording pill are about a quarter smaller.

## Settings and startup

- Start with Windows applies immediately. There is nothing to save, and Save never changes it. If
  you turned Scribe off in Task Manager or Windows Settings, the switch says so and points you there.
- Turning AI cleanup on or off from the tray is never undone by a later Settings save: whichever
  choice you made last, in the tray or in Settings, wins.
- Settings opens faster and stays responsive: each section loads in the background and shows what
  it is waiting for, and rating a result never freezes the window.
- The usage page no longer repeats work when you change the period quickly.

## AI cleanup

- Browsing the Foundry Local model list no longer downloads anything. Only setting it up, loading a
  model, or saving with AI cleanup on does.
- When you switch to another provider, Scribe removes what Foundry Local downloaded. When you switch
  Foundry Local models, it keeps only the one you chose. A tray notice tells you how much space was
  freed.
- When cleanup fails, Settings still shows the provider's own explanation, while the log and the
  recording pill keep to a short, general reason.
- Editing the writing style, the prompts or your dictionary no longer restarts AI cleanup; the new
  instructions simply apply to your next dictation.
- A model load you cancel, or one that fails, no longer leaves AI cleanup stuck: Scribe resumes the
  cleanup setup you had.
- Microsoft Foundry requests made through the Responses API always ask Azure not to store them.
- A GitHub Copilot model you choose is passed to Copilot directly, and checking the Copilot CLI's
  version can no longer hang.

## Under the hood

- The keyboard hook never waits on anything, so a busy moment cannot make Windows drop it.
- Loading and using the speech models is one step, so an idle release can no longer race a new
  dictation, and a long decode stops promptly when Scribe shuts down.
- Security servicing for the .NET 10 libraries, and newer speech engine and Windows App SDK builds.
- A new suite of speech test scenarios covers long dictations, quiet and noisy audio, silence
  auto-stop and storage, and a quick version of it runs on every build.

Available for Windows x64 and Arm64. Microsoft Store submission is handled automatically; Store
availability follows certification.
