# Scribe 0.3.11

A user reported that dictation cut out after a few seconds. Getting to the bottom of it needed their
log file, and their log file did not exist. This release is about that: the folder is now where
Scribe says it is, the logs record enough to answer a question like that, and sending them takes one
button instead of a folder hunt.

## The log folder was not where Scribe said it was

If you installed from the Microsoft Store, Settings showed your logs at
`%LOCALAPPDATA%\ScribeData\logs`, and that folder did not exist. Copying the path and opening it in
File Explorer got you nowhere, because Windows was quietly storing everything a Store app writes
inside the app's own package folder instead. Scribe could see its files there. You could not.

Two things fix this. The Store package now asks Windows to leave that one folder alone, so it is a
real folder at the path Scribe names, and both installation types keep their files in the same
place. And rather than trusting that, Scribe checks where its files actually landed each time it
starts, and Settings shows you the location that genuinely exists. If you had been running the Store
version, your settings, history and dictionary are carried across on first launch, and the About
page tells you where the older files are so you can still find the logs from before the update.

## Logs now say enough to diagnose something

Every session starts with a summary: the version, how Scribe was installed, your Windows build and
hardware, which microphone and model are in use, and the settings that decide how recording starts
and stops. A recording logs why it ended, whether that was you releasing the key, the silence
auto-stop deciding you had finished, the microphone faulting, or dictation being paused. If the
microphone stream stops on its own part way through, which Windows does not report as an error,
Scribe now notices and says so instead of quietly losing whatever you said next.

Recordings are also measured. Scribe records how loud the capture was, whether it was clipping, and
what each channel of a multi-channel microphone contributed, so a recording that produced no words
can be told apart from one that was simply too quiet, or from a headset whose second channel was
averaging your voice down. Before this, the log could only say that the audio was not completely
silent, which is true of almost any microphone and answers nothing.

None of this includes what you said. Transcripts, dictionary entries, snippets, prompts and API keys
are never written to a log, and configured endpoints are recorded only as configured or not.

## Logs clean up after themselves

Logs are kept for seven days and then deleted. The folder is size limited as well, so a fault that
writes continuously cannot fill your disk, and a single day that runs away is capped without losing
the rest of the week.

## Sending logs is one button

Settings, under About, has **Save diagnostics**. It writes the kept logs and a summary of your PC to
a single zip file wherever you choose, ready to attach to a bug report. Your dictation history and
saved credentials are never included, and the zip contains a plain-text description of what is
inside so you can read it before sharing it.

## Smaller fixes

A microphone that takes a moment to wake up no longer costs you a dictation without explanation. If
opening it is slow, Scribe records how long it took, and a key press too brief to record anything
now tells you that instead of suggesting you change microphones.
