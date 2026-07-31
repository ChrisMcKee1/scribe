# Scribe 0.3.1

A long dictation could lose most of what you said. This release fixes the cause, and two other
things found in the same pass over 22 days of production logs.

## A long dictation could silently lose most of its text

Hold the key for over a minute and the transcript could come back a fraction of its real length,
cut mid-sentence, with no error and nothing in the log. One real 75 second recording produced 166
characters of an approximately 1000 character dictation.

The recording was never the problem. Across 202 dictations in a day, captured audio matched the
time the key was held every single time, to within 0.38 seconds. The audio was complete; the
transcript was not.

The speech recogniser degrades on a long buffer, and not by simply stopping early. Decoding the
first 55 seconds of that recording gave a correct 861 characters. Decoding 65 seconds of the same
recording gave 375, and the missing text came from the **middle**, not the end. Adding more audio
destroyed text that had already been recognised correctly. Appending nothing but digital silence to
the identical 55 seconds of speech walked the result down from 861 characters to 785, 677, 191,
100, and finally 49.

Silence removal was the one thing that would have helped, and it was switched off for exactly these
recordings. Scribe trims leading and trailing non-speech before recognition, but that step was
skipped entirely for any capture over 60 seconds, on the assumption that a longer capture could not
be buffered safely. So the recordings most likely to be damaged were the only ones that kept every
second of silence.

That assumption was wrong and is now gone. Measured across 30 real recordings of 57 to 250 seconds,
the detector reports identical results whatever buffer size it is given, and never holds more than
31 seconds of audio at once because Scribe drains it continuously.

On the recordings that failed, decoded through the real pipeline:

| recording | before | after |
|---|---|---|
| 75 s | 357 | 857 |
| 61 s | 857 | 973 |
| 96 s | 840 | 1096 |
| 127 s | 1738 | 1880 |
| 141 s | 1673 | 1867 |

Being straight about the limit: this removes silence at the **edges**. A long recording whose first
and last words sit near the very start and end, with all its pauses in the middle, can still hit the
underlying recogniser behaviour. Short and medium dictations are completely untouched by this
change.

## Push-to-talk was being torn down and rebuilt every few minutes

Scribe watches its own keyboard hook, because Windows removes a low-level hook that misses a
deadline and never says so. That watchdog was wrong 3,775 times over 22 days.

It timestamped its test keystroke after sending it, but the hook records the keystroke while the
send is still running. The answer therefore always looked older than the question, and roughly one
check in eight declared a perfectly healthy hook dead. Every one of those rebuilt the hook thread
and cleared held-key state, which can stop a dictation that is in progress.

The check no longer uses a clock. It counts hook events and asks only whether the count moved,
which cannot be fooled by the order the two threads happen to run in.

## Profiles now come with working examples

Per-app profiles could already change writing style and line-break handling per application, but you
started from an empty row with no hint of what to type. **Add** now offers ready-made templates:

- **Terminals and shells**, which keeps a dictation on one line so it is not run early, and asks AI
  cleanup for plain text.
- **AI chat and agents** for Claude, ChatGPT, GitHub Copilot, Microsoft 365 Copilot and Scout,
  which stops a multi-paragraph dictation being sent as several separate messages.
- **Microsoft Teams**, kept separate because Teams normally accepts a soft line break already, so
  try it without this first.
- **IDE integrated terminals**, which carries a warning: a process name cannot tell an editor from
  its integrated terminal, so it removes line breaks in your source files too.
- **Documents** for Word, Excel, PowerPoint, OneNote and Notepad, which keeps real paragraph breaks
  even when the global setting flattens them.

Nothing is added to your settings automatically. Upgrading changes none of your existing behaviour.

## A failed dictation could vanish without telling you anything

If the recogniser returned nothing for a recording that clearly contained speech, Scribe did
nothing at all: no message, no sound, no mark in the log above routine information. The overlay
simply closed and the dictation was gone. The only way to notice was to see that nothing had been
typed. This happened 34 times over 22 days and was never once reported.

Every other failure already announced itself, including a muted microphone, a disconnected
microphone, a failed insertion, and a failed cleanup. This one path was missed because it only
triggers when the microphone worked correctly and the recogniser still produced no words.

It now records a warning and shows the failure on the overlay, so a lost dictation is something you
see rather than something you discover later.

## Recognition that collapses to a single word is now recorded

Occasionally a recording of several seconds comes back as one word, most often "Yeah.". That is the
same underlying recogniser failure as an empty result, except it reaches your document looking like
a normal dictation. Scribe now measures the text against the amount of voice actually detected in
the recording, ignoring pauses, and writes a warning when the result is implausibly short.

Nothing is discarded and nothing is shown to you, because a short answer can be perfectly correct
and the current measurement cannot yet tell the two apart with confidence. This release makes the
occurrences findable so the behaviour can be pinned down properly.

## Both architectures

x64 and Arm64 are built from the same source and shipped together, and the Arm64 payload is checked
for architecture purity at packaging time.
