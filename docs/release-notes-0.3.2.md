# Scribe 0.3.2

Two failures that told you nothing, and a straight answer to "where does Scribe keep my files?".

## A failed dictation could vanish without telling you anything

If the recogniser returned no words for a recording that clearly contained speech, Scribe did
nothing at all: no message, no sound, and nothing in the log above routine information. The overlay
simply closed and the dictation was gone. The only way to notice was to see that nothing had been
typed where you were expecting it.

The check that should have caught this only asks whether the microphone was silent. When the
microphone worked correctly and the recogniser still produced nothing, that check passed and the
failure fell through in silence. Every other failure already announced itself, including a muted
microphone, a disconnected microphone, a failed insertion, and a failed cleanup.

This happened 34 times over 22 days of real use and was never once reported. It now records a
warning and shows the failure on the overlay, so a lost dictation is something you see rather than
something you find out about later.

## Recognition that collapses to a single word is now recorded

Occasionally a recording of several seconds comes back as one word, most often "Yeah." That is the
same recogniser failure as an empty result, except it reaches your document looking like a perfectly
normal dictation.

Scribe now compares the amount of text against how much voice was actually detected in the
recording, ignoring the pauses, and writes a warning when the result is implausibly short for what
was said.

Nothing is discarded and nothing is shown to you. A genuinely short answer can be completely
correct, and the current measurement cannot yet tell the two apart with confidence, so acting on it
would risk crying wolf on good dictations. This release makes the occurrences findable so the
behaviour can be pinned down properly first.

## About now tells you where your files are

The About page lists the two locations that matter, filled in with the real paths for your PC:

- **Diagnostic logs**, one file per day, recording app events, timings and errors. Never your audio
  and never your transcripts.
- **Dictation history and settings**, a single database file that also holds your dictionary,
  snippets and settings, plus recorded audio when "Store audio history" is on.

Each has a **Copy** button for pasting the path elsewhere, and an **Open** button that opens the
folder in File Explorer. Both paths are read from the same place the app writes to, so they cannot
drift from reality, and a portable or Store installation shows its own correct location.
