# Scribe AI Privacy Policy

**Effective date:** September 24, 2026
**Publisher:** Chris McKee

This Privacy Policy applies to Scribe AI, also known as Scribe, a Windows voice
dictation application.

## Summary

Scribe is designed to perform voice capture and transcription on your Windows
device. Scribe does not require a Scribe account, does not contain advertising,
and does not sell personal information.

The publisher does not operate a service that receives your microphone audio or
dictation history. Scribe does, however, access and store personal information
locally to provide its features. If you choose an online AI provider, certain
text described below is sent to the provider you configure. Audio is never sent
to an AI provider.

## Information Scribe accesses and stores

### Microphone audio

Scribe accesses the microphone you select when you start a dictation. Audio is
processed locally to detect speech and create a transcript.

By default, captured audio is held in memory and discarded after processing. If
you enable audio history, Scribe stores recorded audio locally with the
corresponding history entry for up to seven days, and keeps no more than 250 MB
of recordings in total, removing the oldest first once that limit is reached.
Deleting the entry or clearing your history removes its recording sooner. When a
recording is removed, the entry's text stays, as described under Dictation
history.

Scribe never transmits microphone audio off your device.

### Reporting an AI result

If AI cleanup or a rewrite produces something inappropriate, you can report it
from the History page or from Settings, About. Scribe composes the report and
opens your email app with it, or copies it to your clipboard. Scribe does not
send anything itself, and nothing leaves your device unless you send it.

The report contains the AI result, the model and provider that produced it, your
Scribe version, and the time. It does not include your audio, any other
dictation, or what you originally said, unless you choose to add that yourself
before sending. You can read the whole report before deciding.

Rating a result useful or not useful is stored only on this PC. Those ratings are
never transmitted, and they are separate from Microsoft Store ratings and
reviews, which are handled entirely by the Store.

### Dictation history and usage information

Scribe stores completed transcripts locally so that you can review and recover
recent dictations, view usage information, and receive local dictionary
suggestions. A history entry may include:

- The transcript or final processed text
- The date and time of the dictation
- The name of the application that had focus
- Audio and processing durations
- The speech or AI model used
- Recorded audio, only when audio history is enabled

Transcript history remains on the device for the history retention period you
choose in Settings (90 days unless you change it; 0 keeps history until you
delete it), and you can delete individual entries or clear the history at any
time. If Scribe starts without your saved settings, because they could not be
read or were lost when a damaged database was repaired, it deletes no transcript
history for as long as it runs without them, which after such a repair lasts
until you review and save your settings.

### Dictionary, snippets, profiles, and settings

Scribe locally stores information that you provide, including dictionary terms,
replacement text, snippet templates, per-application profiles, writing-style
instructions, hotkey choices, provider configuration, and other preferences.
Imported dictionaries and libraries are also stored locally.

### Cleanup failure samples

When optional AI cleanup fails, Scribe may retain a shortened sample of the
unprocessed transcript, together with failure details, to help diagnose and
improve the local cleanup configuration. These samples are stored locally and
are deleted automatically after seven days, whether or not later cleanups
succeed. You can also clear them from the application.

### Clipboard and keyboard access

Scribe listens for the global hotkey or key combination you configure. It uses
those key events only to start and stop dictation and does not record the text
you type.

If clipboard-paste injection is selected or used as a fallback, Scribe may
temporarily read the existing text clipboard so it can restore that content
after pasting the dictation. Scribe does not retain or transmit the previous
clipboard content. Scribe restores your previous clipboard text only when it can
confirm the clipboard still holds what Scribe placed there, so anything you copy
during a dictation is kept. Scribe's own clipboard item carries a small random
marker so Scribe can recognize it; the marker contains no data.

Clipboard writes that Scribe performs itself are marked so Windows excludes them
from clipboard history (Win+V) and from cross-device cloud clipboard sync.

### Diagnostic information

Scribe writes diagnostic logs locally. Logs may include application lifecycle
events, the selected audio device, the name of the focused application,
performance measurements, model and provider configuration identifiers, and
error details. Scribe does not write microphone audio, transcripts, dictionary
entries, snippet contents, custom dictionary library names, custom prompts, API
keys, or service-principal secrets to its diagnostic log files. Configured
endpoint addresses, and Azure deployment, account and subscription names, are
recorded only as configured or unset, never as the value itself. When something
fails in the Scribe app itself (Settings, the tray, quick add and the dictation
pipeline) or in the parts of Scribe that talk to AI providers or handle what they
return, the log records the failure by its kind only: the type of error, status
and error codes, and, for a failure that points to a defect in Scribe, the places
in Scribe's code where it happened. It never records the error's message, which
can quote an endpoint, an account or resource name, or text from a report you
chose to send. The one exception is Foundry Local's own diagnostic messages, which
are kept, with your user profile folder replaced by a placeholder, because they
are needed to diagnose on-device hardware problems. In parts of Scribe that never
talk to a provider, such as audio capture and local storage, the message of a
local error can still appear in the log.

Earlier versions of Scribe wrote some of this information into their logs in a
small number of known formats: the recognized text of each dictation (versions
0.3.11 to 0.4.2), the host name or address of a custom AI cleanup endpoint
(0.1.7 to 0.4.2), Azure deployment, account and subscription names (0.1.0 to
0.4.2), invalid dictionary entries and snippet phrases, dictionary library names
and paths, and per-app profile names (0.1.0 to 0.4.2), failure text returned by
AI providers (0.1.0 to 0.4.2), the error text attached to Settings warnings about
Azure sign-in, subscriptions, deployments, Azure CLI and credential checks (0.2.4
to 0.4.2), and the text of an AI output report when no mail app could open it,
which can include dictation you chose to include in the report (0.3.14 to
0.4.2). Current versions replace those values with a
placeholder wherever they appear in a known format: in log files kept from
earlier days, which Scribe rewrites once, at startup and after midnight, never
while Scribe may still be adding to a file; and in the copies of the logs that
Save diagnostics puts in its zip. A small file named `redaction-ledger.txt` in the
log folder records which log files were already checked, by name, size and time
only. Values written in any other format are not changed.

Diagnostic logs are kept for seven days and then deleted automatically. The log
folder also has soft size budgets of about 16 MB per day and 64 MB in total:
once a day's file passes its budget only warnings and errors are added to it for
the rest of that day, and the oldest days are removed when Scribe starts and at
midnight, so the folder can briefly exceed the total until the next sweep. You
can delete the logs yourself at any time from the folder shown in Settings,
under About.

Settings, under About, includes "Save diagnostics", which writes the kept log
files and a summary of your PC into a single zip file at a location you choose.
That file is intended to be attached to a bug report. It never includes
`scribe.db`, which holds your dictation history and saved credentials. The zip
contains a `report.txt` describing exactly what is inside, so you can read it
before sharing it with anyone. The logs in the zip have the values described
above replaced, and `report.txt` lists the recognized formats, the versions that
wrote them, and how many values of each kind were replaced.

Advanced users may configure an OpenTelemetry endpoint through the
`OTEL_EXPORTER_OTLP_ENDPOINT` environment variable. When configured, Scribe
sends performance traces, which may include the focused application name,
timings, character counts, and error information, to that user-selected
endpoint.

## Optional AI features and data transmission

AI features are optional. The default Foundry Local provider runs on the device,
so the text it cleans, its instructions and your vocabulary stay on the device.

If you turn on AI cleanup with Microsoft Foundry, an OpenAI-compatible endpoint,
or GitHub Copilot, every cleanup request sends that provider:

- The text Scribe recognized for the dictation, before your dictionary and
  snippets are applied to it. A long dictation can be sent in several parts,
  each with everything listed below.
- Scribe's cleanup instructions, including your writing style or, when a
  per-application profile matches the focused application, that profile's
  writing style. The name of the application is not sent.
- Your vocabulary: the enabled entries of your dictionary and of every enabled
  dictionary library, each as its written form and, where that differs, its
  spoken form. Scribe includes this vocabulary whether or not the dictation
  mentions any of it, and whether or not post-processing is switched on, since
  that switch only decides whether the dictionary is applied on this PC. An
  entry whose written form spans more than one line or runs past 100
  characters, such as a signature or an address, is not vocabulary: the
  dictionary still applies it on this PC, but it is not sent. Your own entries
  come first, and the list holds up to 5,000 terms and 24,000 characters (80
  terms when the Local prompt style is in use), with each spoken form put on one
  line and shortened to 100 characters.

Each time AI cleanup connects to such a provider, for example when Scribe starts
with AI cleanup on, when you turn AI cleanup on, or when you save a different
provider or model, Scribe first sends a short test request containing the word
"ok" and the cleanup instructions, with none of your vocabulary. If a Microsoft
Foundry deployment does not accept that request's format, Scribe sends the same
test once more in the other format it supports.

AI cleanup never sends audio, your snippet templates, your dictation history, or
the name of the focused application.

If you request AI dictionary suggestions while AI cleanup runs anywhere but on
this PC, Scribe first asks, naming where the request goes, then sends its
standard suggestion request and up to 6,000 characters of your most recent
dictations as they were inserted, which can include text your dictionary and
snippets added. It sends them only to the provider it named: if your AI cleanup
provider changes before the request goes out, nothing is sent. It does not send
your dictionary itself or your writing style. If you request an AI usage insight,
Scribe sends aggregate usage totals and the labels of recurring terms your
dictionary covers, leaving out any label whose replacement text spans more than
one line or is longer than 100 characters. It sends no transcripts, audio,
focused application names, or dictation timestamps.

The remote provider processes this information under the account, terms, data
retention settings, and privacy policy associated with that provider. Depending
on your configuration, the provider may be Microsoft, GitHub, or the operator of
an OpenAI-compatible endpoint. The publisher of Scribe does not receive this
information. For Microsoft Foundry, Scribe asks the service not to store its
responses, but Microsoft's abuse monitoring can still keep a sample of prompts
and responses it flags for review, as Microsoft's data privacy documentation for
Foundry models describes.

The GitHub Copilot provider differs from the others in how it connects. There is
no endpoint you configure and no key Scribe stores. Scribe runs the GitHub
Copilot CLI that is already installed and signed in on this device, so requests
travel under your own GitHub identity and are processed by GitHub under your
Copilot subscription terms and privacy policy. Scribe never sees or stores a
GitHub token. Asking Settings to list the models your licence allows also
contacts GitHub. Which model handles a request is whichever one you select, or
your account default when you leave that blank.

You can stop this transmission at any time by turning off AI cleanup, selecting
Foundry Local, not invoking AI suggestions or insights, or removing the remote
provider configuration. For GitHub Copilot, selecting a different provider stops
Scribe using it; signing out of the Copilot CLI removes its access entirely.

## Provider credentials and account information

If you configure a remote AI provider, Scribe may store endpoint addresses,
deployment and model names, Azure tenant, subscription, resource or application
identifiers, and API credentials locally. API keys and service-principal client
secrets are encrypted at rest using Windows Data Protection API protection
bound to your Windows user account.

When you use Microsoft Foundry setup or discovery, Scribe communicates with
Microsoft services using the credentials and account you select. Microsoft
processes that information according to the terms and privacy policy applicable
to your Microsoft account and services.

## Network downloads and updates

When Scribe checks for or downloads application updates, models, or supporting
components, the service hosting that download may receive ordinary network
information such as your IP address, request time, and requested file. Depending
on the installation and feature used, these services may include Microsoft,
GitHub, or a model publisher's hosting service. No microphone audio or
transcript content is included in these requests.

## Storage and security

Scribe stores application data under the current Windows user's local
application-data directory, normally:

`%LOCALAPPDATA%\ScribeData`

Access to these files is controlled by Windows user-account permissions and any
device-encryption protections configured in Windows. Scribe does not separately
encrypt transcript history, optional stored audio, dictionary content, snippets,
profiles, or diagnostic logs. Provider API keys and service-principal secrets
receive the additional Windows Data Protection API protection described above.

When Scribe deletes a history entry, a recording, or a cleanup failure sample,
whether you delete it or its retention period ends, the database overwrites the
deleted content with zeros (SQLite's secure delete). The overwrite is written
to the database's write-ahead log (`scribe.db-wal`) first, so until Scribe
copies that log into `scribe.db` and empties it, either file can still hold an
earlier copy of the deleted content. Storage maintenance does both at the end
of its next pass: normally within a minute of your deleting history or clearing
the cleanup failure samples, and at the end of the pass that removes something
because its retention period ended. When other work in Scribe, such as a
dictation or a settings save, interrupts maintenance, it does not empty the log
until it tries again: two minutes later at first, twice as long after each
further interruption, and never more than an hour later. If the database is in
use at that moment, maintenance tries again shortly after, up to three times,
and then hourly. Scribe also empties the log when it closes normally. Secure
delete applies to everything Scribe deletes from its database, dictionary
entries, snippets and profiles included, but Scribe does not empty the log
specially after those deletions, so an earlier copy can stay there until the
log is next emptied.

This has limits. Scribe versions up to 0.4.3 did not overwrite deleted content,
so what they deleted can remain in unused space inside the database file until
the database writes over that space or removes it from the file. And deletion
inside the database does not reach copies made elsewhere, such as the damaged
copies described below, backups, or data the storage device itself keeps.

If Scribe finds its database damaged when it starts, it rebuilds the database
from whatever can still be read and keeps the damaged file beside it, named
`scribe.db.corrupt-` followed by the date and time, so it can be recovered by
hand. The most recent such copy is kept until you delete it. Older copies are
deleted automatically 14 days after Scribe first finds them. These copies can
contain the same history, recordings, settings and saved credentials as the
database itself, with credentials still protected as described above.

If you use the on-device Foundry Local provider, the Foundry Local runtime and
models Scribe downloads are stored in `%USERPROFILE%\.Scribe` (for an isolated
profile started with the `SCRIBE_DATA_DIR` environment variable, in a `foundry`
folder inside that data folder instead). Scribe removes them after you save a
different AI cleanup provider; files that are still in use are removed the next
time Scribe starts.

Because dictated material may include confidential, health, financial, or other
sensitive information, you should use the device security and AI-provider
settings appropriate for the material you dictate.

## Your controls and choices

You can:

- Choose when Scribe accesses the microphone by starting and stopping dictation
- Disable AI cleanup or select the on-device Foundry Local provider
- Turn off dictionary entries or libraries you do not want sent to a remote AI
  provider as vocabulary (a term that is off is also no longer applied on this
  PC)
- Avoid invoking AI dictionary suggestions and AI usage insights
- Disable audio history
- Review and delete individual history entries
- Clear dictation history and cleanup failure samples
- Change or remove dictionaries, snippets, profiles, credentials, and provider
  settings
- Remove local diagnostic logs and other local application data
- Revoke microphone permission through Windows privacy settings

Uninstalling Scribe may not remove data stored outside the application's package
container. To remove all remaining local Scribe data, delete
`%LOCALAPPDATA%\ScribeData` after closing and uninstalling Scribe. This
permanently removes local history, optional stored audio, settings, credentials,
and logs. If you used Foundry Local, also delete `%USERPROFILE%\.Scribe`, which
holds the Foundry Local runtime and models Scribe downloaded; saving a different
AI cleanup provider also removes them, as described under Storage and security.

Because the publisher does not receive or possess your locally stored content,
the publisher generally cannot view, export, correct, or delete that content for
you. Those actions are performed on your device. Requests concerning
information retained by a remote AI provider must be directed to that provider.

## Sharing and sale

The publisher does not sell personal information and does not share it for
advertising or cross-context behavioral advertising.

Scribe discloses text to a remote AI provider only when you enable or invoke an
optional feature that requires the provider, as described above. Scribe may also
send diagnostic traces to an OpenTelemetry endpoint only when an advanced user
explicitly configures one.

## Children

Scribe is a general-purpose productivity application and is not directed to
children under 13. The publisher does not knowingly collect personal
information from children through a Scribe-operated online service.

## Changes to this policy

This policy may be updated when Scribe's features or data practices change. The
effective date at the top identifies the latest revision. Material changes will
be published at this location.

## Contact

For privacy questions, open an issue at:

<https://github.com/ChrisMcKee1/scribe/issues/new>

Do not include transcripts, audio, credentials, or other sensitive personal
information in a public GitHub issue.
