# Microsoft Store submission checklist

This is the working submission guide for publishing Scribe AI as an MSIX app. It records the
recommended Partner Center answers, listing copy, certification notes, and remaining engineering
checks so a submission does not depend on memory.

Last reviewed: July 27, 2026.

## Readiness summary

| Area | Status | Action |
|---|---|---|
| Reserved product name | Ready | Use **Scribe AI** in Partner Center. |
| Privacy policy | Ready | Use the public `PRIVACY.md` URL listed below and answer **Yes** for personal information. |
| Store-managed updates | Ready | Packaged Store installs now bypass the Velopack/GitHub updater. |
| Package build | Ready | Store identity, reserved display name, and public publisher are recorded in `Directory.Build.props`. |
| Restricted capability | Conditional | Explain `runFullTrust` in certification notes. Suggested copy is below. |
| Generative AI declaration | Required | Select **This product incorporates generative AI features**. |
| Automatic cloud backup | Required choice | Turn off automatic OneDrive backup because local history may contain sensitive dictated text. |
| Screenshots | Refresh needed | Nine existing screenshots meet the Desktop size requirement, but they show an older build and navigation. Capture the final UI before upload. |
| Accessibility declaration | Not ready | Do not claim the Store accessibility declaration until a dedicated accessibility test pass is complete. |
| Local-data security | Review | Transcript history and optional audio use Windows profile and device protections but are not separately application-encrypted. |
| Final package validation | Not started | Run the Windows App Certification Kit against the final package before upload. |

## Product identity

- Store product name: **Scribe AI**
- Installed application name: **Scribe**
- Publisher display name: **McKee AI Solutions**
- Package type: **MSIX**
- Device family: **Windows.Desktop**
- Architecture: **x64 and arm64** (submitted as one `.msixbundle`)
- Package/Identity/Name: **53984VeteranApps.ScribeAI**
- Package/Identity/Publisher: **CN=A4B26056-B631-480C-912C-5EF24F1CBD6B**
- Package family name: **53984VeteranApps.ScribeAI_e3jkm6dfkwwbm**

Partner Center assigns the technical identity values. They must match the product identity page
exactly, including capitalization, punctuation, and spaces. The build script reads the assigned
values above from `Directory.Build.props`, so the normal Store build command is:

```powershell
./build/pack-msix.ps1
```

The `VeteranApps` segment is an opaque part of the existing product's technical identity. It does
not control the customer-facing publisher name. The Store listing displays **McKee AI Solutions**.
Do not delete and recreate the product merely to change this internal identifier.

## Pricing and availability

Recommended initial settings:

- Markets: all markets where an English-language listing is acceptable
- Visibility: available and discoverable in the Store
- Audience: public
- Price: free
- Free trial: none
- Sale pricing: none
- Organizational licensing: allow
- Publishing hold: **Do not publish until I select Publish now**

The manual publishing hold lets certification complete without making the listing public before
the final listing, links, installation, and update behavior have been checked.

## Properties

### Category

- Primary category: **Productivity**
- Secondary category: **Utilities & tools**, if Partner Center offers it for this submission
- Display mode: none
- Pen and ink: no
- Game declarations: not applicable

### Privacy and support

- Accesses, collects, or transmits personal information: **Yes**
- Privacy policy:
  `https://github.com/ChrisMcKee1/scribe/blob/main/PRIVACY.md`
- Website:
  `https://github.com/ChrisMcKee1/scribe`
- Support:
  `https://github.com/ChrisMcKee1/scribe/issues/new`

Do not put transcripts, audio, credentials, or other sensitive information in a public GitHub
issue. The in-app About page repeats this warning.

### Product declarations

- Purchases: no purchases or subscriptions
- Accessibility: leave unchecked until Scribe has passed keyboard, Narrator, high contrast,
  magnifier, High DPI, and accessibility-inspection testing
- Install to alternate drives: allowed
- Automatic OneDrive backup: turn off
- Generative AI: **Yes**

Scribe's optional cleanup and vocabulary features generate or transform text with local, Microsoft
Foundry, or user-configured AI models. Microsoft's declaration applies whether the model is local,
cloud-hosted, or supplied by a third party.

### System requirements

- Operating system: Windows 11
- Architecture: x64 and arm64 (Arm64 covers Snapdragon / Copilot+ PCs)
- Microphone: required
- Keyboard: required for the default push-to-talk workflow
- Internet connection: not required for dictation
- Internet connection: required only for downloads, updates, and optional remote AI providers

The package currently declares Windows Desktop build `10.0.19041.0` as its minimum. Before the
final package, decide whether to support that Windows 10 minimum or raise it to Windows 11 build
`10.0.22000.0` so the package and marketing promise match.

## Age rating

Complete the IARC questionnaire from the application's actual content, not the expected audience.
For the current build:

- Category: productivity or utility application
- No violence, sexual content, gambling, controlled substances, or developer-supplied profanity
- No public social network, chat room, or user-to-user content sharing
- Scribe processes text supplied by the user but does not publish it to other users
- Optional AI cleanup transforms the user's own text and should be disclosed wherever the
  questionnaire asks about generated or dynamic content

IARC determines the final regional ratings. Partner Center shares the publisher display name and
email address with IARC during this process.

## Packages

Upload the single bundle produced under `releases`:

`Scribe-<version>.msixbundle`

It contains both `Scribe-<version>-win-x64.msix` and `Scribe-<version>-win-arm64.msix`. Upload the
**bundle**, not the individual `.msix` files: one bundle is one submission that serves every device,
and Windows downloads only the architecture the customer's PC actually needs.

Before upload:

1. Confirm Identity Name and Publisher exactly match Partner Center.
2. Confirm the four-part package version ends in `.0` and is greater than the prior Store version.
3. Confirm the bundle targets Windows.Desktop and contains exactly one x64 and one arm64 package.
   Every package in a bundle must be identical apart from `Identity/ProcessorArchitecture`.
4. Run the Windows App Certification Kit.
5. Install and test the package using an appropriate local test-signing workflow.
6. Verify microphone capture, global hotkey handling, text injection, the tray, the overlay process,
   settings, restart, and uninstall.

### What changes in Partner Center when Arm64 is added

There is **no "supports Arm64" checkbox**. Architecture support is inferred entirely from the
packages you upload, so the work is upload-side, not settings-side:

- **Packages page.** Uploading the `.msixbundle` makes Partner Center list both an x64 and an arm64
  package under the submission. Confirm both appear; if only x64 shows, the bundle did not build
  correctly and the Store will keep serving x64 to Arm devices under emulation.
- **Do not delete the previous x64-only package** until the bundle is validated. The Store ranks by
  version then architecture, so a bundle at a higher version supersedes it cleanly.
- **Availability, Device families.** Confirm **Windows 11 Desktop** remains selected. There is no
  separate Arm device family to tick; `Windows.Desktop` covers Arm64 desktops.
- **Properties, System requirements.** These are free-text minimum-spec fields and do not gate
  architecture, but update the listing copy so Arm users can tell the app is native.
- **Store listing.** Mention native Arm64 / Copilot+ support in the description; this is the only
  place a customer can actually see it before installing.
- **Product declarations.** Nothing architecture-related to change. Leave the existing declarations
  (generative AI, backup, alternate drives) as they are.

Nothing about the reserved name, identity, publisher, age rating, or pricing changes.
7. Verify Settings reports that updates are managed by Microsoft Store.
8. Confirm no GitHub update is downloaded or applied by a Store-installed build.

The package declares:

- `microphone`, required to capture dictation audio
- `runFullTrust`, required for the packaged WPF/Win32 application

## Store listing

### Product name

Scribe AI

### Short description

Private push-to-talk voice dictation for Windows. Speech recognition runs on your PC, works
offline, and types polished text into any app.

### Description

Scribe AI turns your voice into text anywhere on Windows. Hold your chosen key, speak, and release.
Punctuated text appears in the application that already has focus.

Speech recognition runs locally on your CPU. No account or subscription is required, and
microphone audio never leaves your device. Scribe can work without an internet connection.

Build a personal dictionary for names and technical terms, save reusable voice snippets, and use
per-application profiles to change writing style and line-break behavior. Local history keeps
recent dictations recoverable and powers private usage insights.

AI cleanup is optional. Use an on-device Foundry Local model, your own Microsoft Foundry
deployment, or an OpenAI-compatible endpoint that you configure. Remote providers receive
transcribed text and related instructions, never microphone audio. Turn AI cleanup off at any time
to keep the complete dictation pipeline local.

### Product features

Enter these as separate features without adding bullet characters:

1. Offline speech recognition with no Scribe account
2. Push-to-talk and hands-free toggle modes
3. Types into your current Windows application
4. Multilingual dictation with automatic language handling
5. Personal dictionary and vocabulary libraries
6. Voice-triggered reusable snippets
7. Per-application writing profiles
8. Optional local or bring-your-own AI cleanup
9. Local dictation history and recovery
10. Private local usage and performance insights
11. Configurable recording overlay
12. Microphone audio never leaves your device

### Search terms

Microsoft Store policy permits no more than seven relevant terms or phrases:

1. voice dictation
2. speech to text
3. voice typing
4. offline dictation
5. push to talk
6. transcription
7. productivity

### What's new

Leave this blank for the first submission. Use it for subsequent Store updates.

## Screenshots

Desktop screenshots must be PNG files at least 1366 by 768 pixels and no larger than 50 MB.
Microsoft requires one and recommends at least four. The existing full Settings screenshots are
2170 by 1663 and meet the technical minimum. They show an older build, so recapture the chosen
screens after the Store-facing UI is final. `pill.png` is too small to submit by itself.

Recommended order and captions:

1. `settings-general.png`: Choose your microphone, push-to-talk keys, and local speech model.
2. `overlay.png`: Place the compact recording indicator anywhere around your screen.
3. `ai-foundry-local.png`: Keep optional AI cleanup on your PC with Foundry Local.
4. `dictionary.png`: Teach Scribe names, acronyms, and technical vocabulary.
5. `snippets.png`: Expand a spoken trigger into a reusable block of text.
6. `profiles.png`: Adapt writing style and line breaks to each application.
7. `history.png`: Review, recover, copy, or delete recent dictations stored locally.
8. `usage.png`: Understand local usage trends without surveillance.
9. `diagnostics.png`: Verify recognition and cleanup performance on your own hardware.

Before upload, inspect every screenshot for real names, transcripts, tenant IDs, endpoints,
subscription names, API keys, or other personal information.

## Suggested certification notes

Paste and adjust the following text. Keep the date current:

> July 27, 2026. Scribe AI is a Windows tray application and requires no account or network
> connection for its primary dictation workflow. After launch, open the Scribe microphone icon in
> the notification area and choose Settings. Hold Right Ctrl, speak, and release to test dictation.
> The key can be changed in Settings. Speech recognition runs locally and audio is never
> transmitted. Optional AI cleanup is off by default and is not required for certification.
>
> The package declares microphone access for user-initiated dictation. It declares runFullTrust
> because Scribe is a packaged WPF/Win32 desktop application that installs a user-configured global
> push-to-talk hook, captures microphone input, inserts Unicode text into the foreground desktop
> application, maintains a tray icon, and launches its separate recording-overlay process. It does
> not request elevation or install a service or driver.
>
> To quit, open the tray menu and select Quit. Privacy policy:
> https://github.com/ChrisMcKee1/scribe/blob/main/PRIVACY.md

## Final submission sequence

1. Finish Partner Center Properties, including privacy, support, generative AI, and backup choices.
2. Generate the IARC age rating.
3. Obtain the exact package identity and publisher values.
4. Build the final MSIX without changing the application version unless a version bump is approved.
5. Run the Windows App Certification Kit and complete package smoke tests.
6. Upload the package and confirm Partner Center's architecture, device family, capabilities, and
   version interpretation.
7. Add the English listing, at least four screenshots, icon assets, captions, and search terms.
8. Add certification notes and keep the manual publishing hold enabled.
9. Submit for certification.
10. After certification, test acquisition using the Store path before selecting Publish now.

## After publication

- Add the Microsoft Store product URL to the in-app About page.
- Make the Store button the primary installation path in the README.
- Keep GitHub visible for source, stars, issues, releases, and the privacy policy.
- Preserve the direct GitHub installer for users who deliberately choose that channel.
- Monitor Partner Center acquisition, health, ratings, reviews, and certification reports.

## Microsoft references

- [Create an MSIX app submission](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/create-app-submission)
- [Pricing and availability](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/price-and-availability)
- [App properties](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/enter-app-properties)
- [Product declarations](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/product-declarations)
- [Age ratings](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/age-ratings)
- [Upload packages](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/upload-app-packages)
- [Package requirements](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/app-package-requirements)
- [Store listing](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info)
- [Screenshots and images](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/screenshots-and-images)
- [Submission options and certification notes](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/manage-submission-options)
- [Microsoft Store policies](https://learn.microsoft.com/windows/apps/publish/store-policies)
