# Scribe AI Privacy Policy

**Effective date:** July 27, 2026
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
corresponding history entry until you delete that entry or clear your history.

Scribe never transmits microphone audio off your device.

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

Transcript history remains on the device until you delete individual entries or
clear the history.

### Dictionary, snippets, profiles, and settings

Scribe locally stores information that you provide, including dictionary terms,
replacement text, snippet templates, per-application profiles, writing-style
instructions, hotkey choices, provider configuration, and other preferences.
Imported dictionaries and libraries are also stored locally.

### Cleanup failure samples

When optional AI cleanup fails, Scribe may retain a shortened sample of the
unprocessed transcript, together with failure details, to help diagnose and
improve the local cleanup configuration. These samples are stored locally and
are pruned after approximately seven days. You can also clear them from the
application.

### Clipboard and keyboard access

Scribe listens for the global hotkey or key combination you configure. It uses
those key events only to start and stop dictation and does not record the text
you type.

If clipboard-paste injection is selected or used as a fallback, Scribe may
temporarily read the existing text clipboard so it can restore that content
after pasting the dictation. Scribe does not retain or transmit the previous
clipboard content.

### Diagnostic information

Scribe writes diagnostic logs locally. Logs may include application lifecycle
events, the selected audio device, the name of the focused application,
performance measurements, model and provider configuration identifiers,
network endpoint addresses, and error details. Scribe does not intentionally
write microphone audio or complete transcripts to its diagnostic log files.

Diagnostic logs remain in Scribe's local data directory until you delete them.

Advanced users may configure an OpenTelemetry endpoint through the
`OTEL_EXPORTER_OTLP_ENDPOINT` environment variable. When configured, Scribe
sends performance traces, which may include the focused application name,
timings, character counts, and error information, to that user-selected
endpoint.

## Optional AI features and data transmission

AI features are optional. The default Foundry Local provider runs on the device
and does not send transcript text to a cloud AI service.

If you enable Microsoft Foundry or an OpenAI-compatible remote provider, Scribe
sends the information needed to perform the action to the endpoint you
configure. This may include:

- The current transcript
- Writing-style instructions and prompts
- Relevant dictionary or glossary terms
- Per-application profile instructions

Audio is never included.

If you request AI dictionary suggestions, Scribe may send a bounded sample of
recent transcript history to the configured AI provider. If you request an AI
usage insight, Scribe sends aggregate usage totals and dictionary-covered term
labels, but not complete transcripts, audio, focused application names, or
dictation timestamps.

The remote provider processes this information under the account, terms, data
retention settings, and privacy policy associated with that provider. Depending
on your configuration, the provider may be Microsoft or the operator of an
OpenAI-compatible endpoint. The publisher of Scribe does not receive this
information.

You can stop this transmission at any time by turning off AI cleanup, selecting
Foundry Local, not invoking AI suggestions or insights, or removing the remote
provider configuration.

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

Because dictated material may include confidential, health, financial, or other
sensitive information, you should use the device security and AI-provider
settings appropriate for the material you dictate.

## Your controls and choices

You can:

- Choose when Scribe accesses the microphone by starting and stopping dictation
- Disable AI cleanup or select the on-device Foundry Local provider
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
and logs.

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
