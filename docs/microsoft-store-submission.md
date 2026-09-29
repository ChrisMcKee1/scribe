# Microsoft Store submission checklist

This is the working submission guide for publishing Scribe AI as an MSIX app. It records the
recommended Partner Center answers, listing copy, certification notes, and remaining engineering
checks so a submission does not depend on memory.

Last reviewed: September 29, 2026, for 0.5.1.

## Readiness summary

| Area | Status | Action |
|---|---|---|
| Reserved product name | Ready | Use **Scribe AI** in Partner Center. |
| Privacy policy | Ready | Use the public `PRIVACY.md` URL listed below and answer **Yes** for personal information. |
| Store-managed updates | Ready | Packaged Store installs now bypass the Velopack/GitHub updater. |
| Package build | Ready | Store identity, reserved display name, and public publisher are recorded in `Directory.Build.props`. |
| Package submission | Automated | `store.yml` builds and submits the package after each release. It does not touch the listing. |
| Restricted capability | Conditional | Explain `runFullTrust` in certification notes. Suggested copy is below. |
| Generative AI declaration | Required | Select **This product incorporates generative AI features**. |
| Automatic cloud backup | Required choice | Turn off automatic OneDrive backup because local history may contain sensitive dictated text. |
| Listing text | Ready for 0.5.1 | The copy below uses the current feature names. It goes out with the next submission; see [Updating the listing](#updating-the-listing-automatically). |
| Screenshots | Ready for 0.5.1 | Ten 1366 by 900 screenshots of the current Settings are in `docs/store/screenshots/`. |
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

## Start with Windows

The MSIX manifest declares `windows.startupTask`, with task ID `ScribeStartup` and executable
`Scribe.exe`. It defaults to disabled so fresh installs remain opt-in. Packaged builds use
`Windows.ApplicationModel.StartupTask`, not `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`:
registry writes from a packaged app can be virtualized and are not a startup registration.
The direct-download build retains the per-user Run entry.

The Start with Windows switch (Settings, Dictation, Startup) applies the moment it is flipped: there
is nothing to save, and Save never changes it. Scribe records the preference first, then asks
Windows, and the switch then shows what Windows reports. If Windows refuses, the switch goes back and
says why. For a packaged desktop app, `RequestEnableAsync` shows no consent dialog.

On the first launch after upgrading, an existing enabled Scribe preference enables the newly
declared task. Beyond that, startup never changes the task: a task disabled in Windows Settings or
Task Manager can only be re-enabled there, a task turned off from Scribe stays off, and
organization policy overrides are also respected. The in-app button opens **Windows Settings >
Apps > Startup**. For the direct-download build, Scribe reads Windows' own record of a Task Manager
or Settings choice for its Run entry (`StartupApproved\Run`) and never writes it; when that record
says off, the switch shows it and points to Windows Settings.

Before submitting, confirm the task is present in both architecture manifests. On a packaged
install, turn the switch on (no Save needed), sign out and back in, and confirm Scribe starts. Also
test disabling it from Windows Settings, reopening Scribe, and saving an unrelated setting: startup
must stay disabled. Building the code alone does not update the manifest of an already installed
Store package; the fix needs a new Store package.

References: [StartupTask API](https://learn.microsoft.com/uwp/api/windows.applicationmodel.startuptask)
and [packaged desktop startup extensions](https://learn.microsoft.com/windows/apps/desktop/modernize/desktop-to-uwp-extensions#start-an-executable-file-when-users-log-into-windows).

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
- Internet connection: required only for downloads, updates, and optional AI cleanup through an online AI service

The package declares Windows Desktop build `10.0.22000.0` (Windows 11) as its minimum, matching
`SupportedOSPlatformVersion` in `Scribe.App.csproj` and the Windows 11 promise in the listing. The
two are deliberately kept equal: a lower MinVersion would let the Store install Scribe on a Windows
10 build where the app is compiled against a higher floor and Foundry Local's WinML integration
cannot acquire execution providers, which fails at first use rather than at install.

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
6. Verify microphone capture, the dictation shortcuts, typing into apps, the tray, the recording
   indicator's process, settings, restart, and uninstall.
7. Verify Settings reports that updates are managed by Microsoft Store.
8. Confirm no GitHub update is downloaded or applied by a Store-installed build.

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

The package declares:

- `microphone`, required to capture dictation audio
- `runFullTrust`, required for the packaged WPF/Win32 application

### Why no folder is exempted from AppData write virtualization

Releases 0.3.11 and 0.3.12 exempted `$(KnownFolder:LocalAppData)\ScribeData` from AppData write
virtualization, which needs the `unvirtualizedResources` restricted capability. The Store denied that
capability (policy 10.6.3, 2026-08-27), and 0.3.13 removed the exemption. Nothing is lost: Windows only
redirects folders a packaged app newly creates, so every install that already has `ScribeData` keeps
using the real path, and only a fresh Store-only install lands in
`%LOCALAPPDATA%\Packages\<family>\LocalCache\Local\`. Scribe probes where its files physically land
(`AppPaths.EffectiveRootDir`) and shows that folder under Settings, About, with buttons to copy the path
and open it, so a user can still find a log file or back up their history.

## Store listing

### Product name

Scribe AI

### Short description

Private push-to-talk dictation for Windows 11. Hold a key, speak, and let go: punctuated text is typed
into the app you're using. Speech recognition runs on your PC and works offline, and your audio never
leaves it.

### Description

Scribe AI turns your voice into text in the apps you use every day. Hold a key, speak, and let go:
punctuated text is typed into the app you're using, whether that's email, chat, a document, a browser,
a code editor or a command window.

Private by design. Speech recognition runs on your PC, with a speech model that comes with Scribe.
Your audio is recognized and then discarded: it is never uploaded, and it is kept only if you turn on
audio history, on your PC. There's no Scribe account, no subscription and no advertising.

Works offline. Dictation needs no internet connection, and about 25 European languages are
recognized automatically, with nothing to choose. Scribe runs natively on Intel, AMD and Arm PCs,
Copilot+ PCs included.

Writes your words your way. Teach Scribe how to write the names, acronyms and jargon you use, and
turn on ready-made word packs for AI, Azure, Microsoft 365, GitHub, .NET, data and more, editing them
word by word. Say a short phrase to type saved text, such as your signature. App profiles give each
app its own line breaks and writing style, and in command windows line breaks become spaces, so a
dictation never runs a command early.

Optional AI cleanup. Turn it on to fix punctuation, drop filler words and keep only what you meant
when you correct yourself. Run it on this PC with Foundry Local, fully offline, or use Microsoft
Foundry, your GitHub Copilot subscription or another AI service you set up. When AI cleanup runs
somewhere other than your PC, each request sends the text Scribe recognized, the cleanup
instructions with your writing style, and the words from your dictionary and the word packs you let
AI cleanup use. Audio is never sent, and you can turn AI cleanup off from the tray at any time.

Always know what happened. The recording indicator appears the moment you press your shortcut, with
level bars that follow your voice, then says Typed, or what went wrong and what to do next. Try
dictation shows what Scribe heard, what it changed and how long each step took. History keeps your
recent dictations on your PC, and if a dictation can't be typed, Scribe keeps it ready to copy from
the tray. Usage and Diagnostics show how much you dictate and how fast Scribe runs, worked out on
your PC.

Made for the way you work. Use any key, two keys together, or the middle, Back or Forward mouse
button as your shortcut, and press and hold or press to start and stop. Text going into Remote
Desktop and virtual machine windows is typed in small batches instead of pasted. Settings and the
recording indicator follow your Windows text size and contrast theme.

Scribe is free and open source under the MIT License.

### Product features

Enter these as separate features without adding bullet characters (up to 20, at most 200 characters
each):

1. Speech recognition on your PC: your audio never leaves it
2. Works offline, with no account or subscription
3. Hold a key, speak, let go: text is typed into the app you're using
4. About 25 European languages, recognized automatically
5. Any key, two keys together, or a middle, Back or Forward mouse button as your shortcut
6. Press and hold, or press to start and stop, with Stop when I stop talking
7. A recording indicator with live level bars that tells you what each dictation did
8. Your words: teach Scribe how to write names, acronyms and jargon
9. Eleven ready-made word packs, editable word by word
10. Voice snippets: say a short phrase to type saved text
11. App profiles: line breaks and writing style for each app
12. Optional AI cleanup on this PC with Foundry Local, or with Microsoft Foundry, GitHub Copilot or another AI service
13. Try dictation: see what Scribe heard, what it typed and how long each step took
14. History on your PC, and recovery for a dictation that couldn't be typed
15. Usage and Diagnostics, worked out on your PC
16. Paced typing into Remote Desktop and virtual machine windows
17. Native on Intel, AMD and Arm PCs, Copilot+ PCs included
18. Follows your Windows text size and contrast theme

### Search terms

Microsoft Store policy 10.1.3 allows no more than seven unique terms or phrases, relevant to the
product, with no pricing terms and no other companies' product names:

1. voice dictation
2. speech to text
3. voice typing
4. voice to text
5. offline dictation
6. speech recognition
7. push to talk

### What's new in 0.5.1

Paste this into **What's new in this version** (plain text, at most 1,500 characters; this is about
1,170):

```text
The recording indicator's level bars now rise and fall with your voice.

Text that was hard or impossible to read is fixed: word pack names on the Dictionary page's Word packs tab, and the descriptions in the App profiles menu in the dark theme.

The Dictionary page's Your words and Word packs tabs are easier to tell apart, each with an icon, a live summary and a line that says what it holds.

In Windows contrast themes, status colors, menu separators and greyed-out menu items no longer show as red, and the tray menu follows a theme change.

New for Microsoft Foundry: Let Microsoft Foundry cache what Scribe sends, in Settings, AI cleanup. It's on, which is how AI cleanup already worked. Turning it off asks Microsoft Foundry not to use its prompt cache for new cleanup requests. That works on GPT-5.6 and later models on Standard deployments; earlier models and provisioned deployments can't turn caching off, so there AI cleanup stops and Scribe types what it hears until you turn the switch back on.

Scribe uses much less memory for each dictation and gives more memory back to Windows when it goes idle.

Everything you set up in 0.5.0 carries over.
```

For a later release, write this from that release's notes in `docs/release-notes-<version>.md`, in the
words Settings uses.

## Screenshots

Desktop screenshots must be PNG files at least 1366 by 768 pixels and no larger than 50 MB. The Store
takes up to 10, requires one and recommends at least four, and each can have a caption of up to 200
characters. Microsoft's guidelines keep the important content in the top two-thirds of an image,
because the Store may lay text over the bottom third, and ask for no added logos, icons or marketing
messages.

The 0.5.1 set is in `docs/store/screenshots/`: ten PNG files at 1366 by 900 of the current Settings
pages, rendered with synthetic demo data. The README's images in `docs/screenshots/` (1240 by 900, and
the smaller recording indicator images) are below the Store's minimum width, so upload only the Store
set. Upload it in this order, with these captions:

1. `01-dictation.png`: Hold a key, speak, let go. Choose your microphone and your shortcuts, including a spare mouse button.
2. `02-ai-cleanup.png`: Optional AI cleanup, on this PC with Foundry Local, or with Microsoft Foundry, GitHub Copilot or another AI service.
3. `03-dictionary.png`: Your words: teach Scribe how to write the names, acronyms and jargon you use.
4. `04-word-packs.png`: Word packs: ready-made vocabulary for AI, Azure, Microsoft 365, .NET and more, editable word by word.
5. `05-try-dictation.png`: Try dictation: see what Scribe heard, what it typed, each change it made and how long each step took.
6. `06-snippets.png`: Voice snippets: say a short phrase and Scribe types the saved text.
7. `07-profiles.png`: App profiles: give each app its own line breaks and writing style.
8. `08-history.png`: History: your recent dictations, kept on your PC, ready to copy or delete.
9. `09-usage.png`: Usage: how much you dictate and in which apps, worked out on your PC.
10. `10-diagnostics.png`: Diagnostics: how fast speech recognition and AI cleanup run on your PC.

Before upload, check every screenshot for real names, dictations, tenant IDs, service addresses,
subscription names, API keys, or other personal information. A new set of screenshots replaces the
old one only through a submission, like the listing text: see
[Updating the listing](#updating-the-listing-automatically).

## Suggested certification notes

Paste and adjust the following text. Keep the date current:

> September 29, 2026. Scribe AI is a Windows tray application and requires no account or network
> connection for its primary dictation workflow. After launch, select the Scribe icon in the
> notification area and choose Settings. Hold Page Down, speak, and release to test dictation
> (Page Up does the same without AI cleanup; on a keyboard without those keys, hold Fn with the
> Down arrow). The keys can be changed in Settings, Dictation. Speech recognition runs locally and
> audio is never transmitted. Optional AI cleanup is off by default and is not required for
> certification.
>
> The package declares microphone access for user-initiated dictation. It declares runFullTrust
> because Scribe is a packaged WPF/Win32 desktop application that installs a user-configured global
> push-to-talk keyboard hook (and a mouse hook only while a shortcut uses a mouse button), captures
> microphone input, types Unicode text into the foreground desktop application, maintains a tray
> icon, and launches a separate process that shows its recording indicator. It does not request
> elevation or install a service or driver.
>
> To quit, open the tray menu and select Quit Scribe. Privacy policy:
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

## Automating submissions (one-time setup)

After the first manual submission, every later release can go to the Store on its own. The
`Store submission` workflow builds the MSIX and submits it, and `release.yml` hands off to it
automatically when a `v*` tag finishes publishing. It needs five repository secrets, set up once as
described below.

**No personal access token is involved.** The Store submission API authenticates with an Entra ID
app registration associated with your Partner Center account.

This works on an **individual** developer account as well as a company one. The documentation
places no account-type restriction on it, and an individual developer who has no Entra tenant can
have Partner Center create one at no cost. The **Manager** role below is assigned to the *Entra
application*, not to you: as Primary Owner of the account you already have full access.

1. **Link a Microsoft Entra ID tenant**, if you have not already: Partner Center > **Account
   settings > Tenants** > *Associate Microsoft Entra ID with your Partner Center account*. If you
   already use Microsoft 365 you have a tenant; otherwise choose *Create a new Microsoft Entra ID
   tenant* on the same page, which is free. **User management does not appear until a tenant is
   linked**, which is the usual reason this looks unavailable.
2. **Add the application**: Account settings > **User management** > **Microsoft Entra applications**
   tab > *Add Microsoft Entra application*. Choose *Create* if the app registration does not exist
   yet. In **Roles applicable to developer programs**, tick **Manager**.
3. **Copy the Tenant ID and Client ID** from that application's page in Partner Center.
4. **Generate the client secret**, either on that same Partner Center page via *Add new key*, or in
   the Azure portal under Microsoft Entra ID > App registrations > your app > **Certificates &
   secrets**. Copy the value immediately; it is shown once. Microsoft caps the expiry at 24 months
   and recommends under 12, so **put a calendar reminder on it**. An expired secret surfaces as a
   failed release, not as a warning.
5. **Find your Seller ID** in Partner Center under Account settings.
6. **Find the Store ID** (12 characters) under your app's Product management > Product identity.

Adding or creating the application requires signing in with a Manager account that also holds
**global administrator** permission on the Entra tenant. On a single-person setup with a
Partner-Center-created tenant that is the same identity, so it is usually invisible; on a tenant
that belongs to an employer it is the step most likely to need somebody else.

Then add all five under repository **Settings > Secrets and variables > Actions**:

| Secret | Where it comes from |
| --- | --- |
| `STORE_TENANT_ID` | Entra application page in Partner Center |
| `STORE_CLIENT_ID` | Entra application page in Partner Center |
| `STORE_CLIENT_SECRET` | Azure portal > App registrations > Certificates & secrets |
| `STORE_SELLER_ID` | Partner Center > Account settings |
| `STORE_PRODUCT_ID` | Product management > Product identity (12-character Store ID) |

Verify with a dry run before trusting it, which creates or updates the draft without sending it for
certification. Use the tag of the release you mean to submit, and run it only while no submission is
in certification (see [Updating the listing](#updating-the-listing-automatically)):

```
gh workflow run store.yml -f tag=v<version> -f draft_only=true
```

Constraints worth knowing before you rely on this:

- **Never mix the two paths for one release.** The rule cuts both ways, and the second half is the
  one that bites on the very first automated run:
  - A submission created **in the Partner Center dashboard** cannot be updated, deleted or
    committed by the API. It fails with *"Ingestion API can only update, delete, and commit
    submissions that are created through the API."*
  - A submission created **through the API** must not be edited in Partner Center, or the API can
    no longer commit it.

  So before the first automated submission, **the product must have no in-progress submission**.
  Delete or discard whatever draft is sitting in Partner Center (the published version is
  unaffected) and re-run; the API then creates its own and can manage it. The workflow detects this
  exact failure and prints the steps. Set the repository **variable** `STORE_AUTO_SUBMIT` to
  `false` to suspend the automation and go back to uploading by hand.
- **The msstore CLI version is pinned to `v0.3.9` on purpose.** v0.4.0 regressed the upload
  progress callback (reads a disposed `FileStream` from a thread pool callback) and fails every
  publish with *"Error while uploading the application package"* the moment the Azure blob upload
  reaches 0%. Confusingly, the upload itself may already have succeeded when it crashes, so the
  package can appear in Partner Center despite the failed run. See
  [msstore-cli#154](https://github.com/microsoft/msstore-cli/issues/154); re-test `latest` once
  the fix ships.
- The API **cannot** be used on a product with mandatory app updates enabled; it returns HTTP 409.
- The app needs **one completed manual submission** first, including the age rating questionnaire.
- Certification still takes as long as it takes. The workflow submits; it does not shorten review.

## Updating the listing automatically

### What is automated today

**`store.yml` submits packages only.** After each release it builds the `.msixbundle` and runs
`msstore publish` with it (`-nc` for a draft), which creates a submission and uploads the bundle. It
never changes the description, product features, search terms, What's new, screenshots, captions or
certification notes. The submission API starts each new submission as "a copy of your last published
submission", so all of those stay as they were last published until a submission changes them. The
0.5.1 text and screenshots on this page reach the Store only when a submission sends them.

### What the submission API can change

A submission's `listings` hold one listing per language, and each listing's `baseListing` carries
`description`, `features` (up to 20), `keywords` (the search terms), `releaseNotes` (What's new) and
`images`. Each image gives its file name in the upload (`fileName`), its `imageType` (`Screenshot` for
a desktop screenshot), its caption (`description`) and a `fileStatus`: Microsoft's sample marks an
image to remove as `PendingDelete` and adds a new one as `PendingUpload`. The submission also carries
`notesForCertification`. Microsoft's steps
([Manage app submissions](https://learn.microsoft.com/windows/uwp/monetize/manage-app-submissions)):

1. Create the submission, or get the one in progress.
2. Change its JSON and send it back with the update call.
3. Put every new file, packages and images alike, in one ZIP, and upload it to the submission's
   `fileUploadUrl`, the Azure Blob Storage address (a shared access signature URL) the API returns for
   that submission.
4. Commit the submission, then check its status until it moves from `CommitStarted` to
   `PreProcessing` (or `CommitFailed`, with the reasons in `statusDetails`).

The privacy policy, website and support contact can't be changed this way: those fields are obsolete in
the API, and the Properties page in Partner Center sets them.

Two Microsoft tools do this:

- **[StoreBroker](https://github.com/microsoft/StoreBroker)**, an open-source PowerShell module over the
  API that Microsoft describes as "actively used within Microsoft as the primary way that many
  first-party applications are submitted to the Store". `New-SubmissionPackage` builds the JSON and
  the ZIP from the packages, listing files and screenshots, and `Update-ApplicationSubmission
  -UpdateListings` replaces the listing, deleting the existing screenshots along the way, so the ZIP
  has to carry every screenshot the listing should keep. It uploads the ZIP and, with `-AutoCommit`,
  commits.
- **msstore**, the Microsoft Store Developer CLI that `store.yml` already runs
  ([commands](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/commands)).
  `msstore submission get` returns the submission JSON and `msstore submission updateMetadata` sends a
  changed copy back. For MSIX apps, `updateMetadata` and `submission update` are equivalent: both send
  the complete submission JSON, so either can change listing text and packages together. The CLI
  documents no command that uploads listing images, and `msstore publish` uploads its own ZIP holding
  the bundle, so adding screenshots to the same submission means uploading one ZIP that holds both;
  try that on a draft before trusting it. Order matters as well: when the app has a published
  submission, `msstore publish` deletes the pending draft and makes a new one from the last published
  submission, discarding metadata already staged in the draft, so listing changes go in after
  `msstore publish --noCommit` and before `msstore submission publish` commits. This follows
  Microsoft's current CLI documentation; `store.yml` pins v0.3.9, so check that version's behavior
  before relying on it.

### When a listing update can go out

- **An API submission is never edited in Partner Center.** Microsoft: if you use Partner Center to
  change a submission you created with the API, "you will no longer be able to change or commit that
  submission by using the API", and it can be left in an error state that only deleting it clears.
  Every submission `store.yml` makes is an API submission, so its listing changes go through the API
  too. Editing the listing by hand in Partner Center means a submission created there, which the
  workflow then can't touch (see the constraints above).
- **One submission at a time.** StoreBroker's documentation says "You can only have one 'pending'
  (e.g. in-progress) submission at any given time", and the API refuses to create a submission (HTTP
  409) when "the current state of the app" doesn't allow it. So while one submission is in
  certification, no second one can start, and the listing update rides the next submission: for
  0.5.1, a listing-only submission once 0.5.1 is published, or the next release's package submission.
  A listing-only submission still goes through certification.

### Wiring it into store.yml (not done yet)

1. Keep the listing copy from this page in a checked-in file, for example
   `docs/store/listing.en-us.json`, beside the screenshots in `docs/store/screenshots/`.
2. Either replace the `msstore publish` step with StoreBroker's `Update-ApplicationSubmission` with
   `-ReplacePackages -UpdateListings -AutoCommit`, so one ZIP carries the bundle and the screenshots,
   or keep `msstore publish --noCommit` and add a step that patches `listings.en-us.baseListing` with
   `msstore submission updateMetadata` before `msstore submission publish`. The second changes the
   text only, until the combined ZIP upload is proven.
3. Run it first with `draft_only`, and check the draft's listing in Partner Center without editing it.

## After publication

- The About page links the Store listing and its rating page (`ScribeLinks`), and the README leads
  with the Store.
- Keep GitHub visible for source, stars, issues, releases, and the privacy policy.
- Keep the direct GitHub installer for people who can't use the Store, such as on managed PCs.
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
- [Manage app submissions with the submission API](https://learn.microsoft.com/windows/uwp/monetize/manage-app-submissions)
- [Microsoft Store Developer CLI commands](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/commands)
- [StoreBroker usage](https://github.com/microsoft/StoreBroker/blob/master/Documentation/USAGE.md)
