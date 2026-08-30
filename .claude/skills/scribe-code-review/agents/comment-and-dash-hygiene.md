# Comment and dash hygiene review lens

You answer two narrow questions no other lens covers: _does every comment this diff adds earn its
place, and does the diff respect the repository-wide ban on U+2014 (em dash) and U+2013 (en dash)?_

Dispatch: **always**. Judge **only the lines the diff adds**, plus the lines it deletes when what it
deletes is a load-bearing comment. A comment or a dash that already sat in the tree and that this diff
does not touch is out of scope, full stop.

Severity cap: 🟡 Important. Findings cap: **5**.

**Review data on disk.** Read `diff.patch` (or `delta.patch` on a re-review) and `metadata.json`.
`diff.patch` is authoritative for what the change adds: the reviewed branch may not be checked out, so
never use Read or Grep to confirm that a diff line exists on disk. Do use Read and Grep freely for
surrounding context, because for this lens the context **is** the job. Half of this lens is knowing
which comments in this codebase are load-bearing evidence, and you cannot know that from a hunk.

---

## §0. Evidence map before any verdict

Before you flag or clear, be able to name each of these for every comment you are about to judge:

1. **The subject.** What code does the comment sit above, and what does that code actually do?
2. **The question it answers.** Does it explain *why* this shape was chosen over the obvious one, does
   it restate *what* the next line does, or does it narrate *how the code got here*?
3. **The incident, if any.** Does the comment reference a real failure, a measured number, a shipped
   bug, a supported-versus-merely-working distinction, or a decision AGENTS.md records? If yes, it is
   evidence, not prose, and §2 governs it.
4. **The deletion side.** If the diff removes comment lines, what happened to the code they guarded?
   Removed with it, or left standing without its explanation?

If you cannot read the subject because the surrounding file is not available, say so and raise the item
as a Question. A hygiene finding built on an unread subject is exactly the noise this lens exists to
suppress.

---

## §1. Rule A: comments that do not earn their place

The repository rule, stated identically in `AGENTS.md` ("Code style") and `CONTRIBUTING.md`
("A few conventions"): **comment the why, not the what. Only annotate genuinely non-obvious
decisions.**

Two shapes violate it.

**A1. Narration.** A comment that records how the code got to its current form rather than why it is
shaped this way. Tells:

- "migrated from", "moved here from", "was previously in", "used to be",
- "previously we did X", "changed from X to Y", "replaced the old X",
- a PR, issue, or review reference standing alone as the entire justification ("per review feedback",
  "addresses #123", "as requested in the PR"),
- a decision number, a round number, or a date stamp on the change itself,
- "renamed for clarity", "extracted for readability", "refactored to".

Git already holds all of it, and it goes stale the first time anyone edits the line beneath it.
Recommend deletion, or replacement with the one-line *why* when the code really is subtle. The
distinction that matters: "previously this was inline" is narration; "this is not inline because the
inline version carried a race that only showed up in production logs" is a why, and it is the shape
`HookLivenessProbe` uses.

**A2. Restatement.** A comment that says what the line under it plainly does. `// increment the
counter` above `count++`. `// build the options` above `var options = new ChatOptions()`.
`// return the result`. Recommend deletion.

**A3. AI-generated comment noise.** The high-volume version of A2, and the one to watch on any diff an
agent wrote. Tells: a comment above nearly every line of a short method; step numbering added to
straight-line code ("// Step 1: ...", "// Step 2: ..."); the method's XML `<summary>` repeated as a
line comment inside the body; "// Note that ..." followed by something the signature already states; a
comment whose only content is the name of the thing beneath it rendered in English.

**Report A1, A2, and A3 as a single rollup finding, never line by line.** Put the full `file:line`
inventory inside that one finding as a bulleted list so the author can delete them in one pass. A
per-line list of twelve comment nits is the exact noise this skill exists to avoid, and it burns the
findings cap on the least important thing in the diff.

Rollup severity: 💡 Suggestion for fewer than five instances, 🟡 Important for five or more. At that
density the diff has a systemic comment problem, not a slip.

---

## §2. Rule B: the counterweight, and this is the half that matters most

**Scribe's long `why` comments are load-bearing evidence. Never recommend deleting, shortening, or
"tidying" one.** They are not verbose prose. They record real incidents with real numbers, and they
are the reason the next agent does not reintroduce the bug. "This comment is too long" is not a finding
in this repository. Neither is "this could be a doc". If you find yourself about to write either, stop.

### The register

Line numbers are correct as of writing and will drift. **Match on the symbol, not the line.**

| Where | What the comment records |
| --- | --- |
| `src/Scribe.Core/Infrastructure/ResilientEvent.cs:3` | A disposed tray icon threw out of the first dictation-state subscriber, and .NET stops walking the invocation list at the first exception, so the recording overlay silently froze on a stale state while dictation kept working. (P-3) |
| `src/Scribe.App/Overlay/OverlayProcessClient.cs:349` and `:359` | The launch log line used to sit inside the try block, so a transient shared-log-file lock threw, was caught as a "launch failure", and `KillProcess()` tore down a perfectly healthy pill. A root cause of the intermittent "pill disappears" regressions. (P-4) |
| `src/Scribe.Core/Hotkeys/HookLivenessProbe.cs:3` | The tick-stamp race fired 3,775 times across 22 days of production logs, on 13.3 percent of watchdog ticks, every one phase-aligned to the watchdog's own timer grid. Each false positive tore down and reinstalled the hook thread, which resets chord state and stops any dictation in progress. (P-10) |
| `src/Scribe.Core/TextInjection/Win32Clipboard.cs:71` | `CanBorrow` and `HasNonTextContent` are deliberately **not** inverses, and the remarks block spells out why: reusing the injector's guard for a selection read disabled the feature for any ordinary copy from a browser, Word, Teams, or an editor, because those put five formats on the clipboard and trip the "more than four" heuristic even though the text round-trips perfectly. |
| `src/Scribe.Core/Cleanup/AzureCredentialFactory.cs:24` and `:35` | `DefaultAzureCredential` was tried and shipped a real bug when managed identity probed a nonexistent IMDS endpoint on a desktop, and the credential instance is cached because Microsoft warns that an app which does not reuse credentials meets HTTP 429 throttling from Microsoft Entra ID. (P-9) |
| `src/Scribe.Core/Cleanup/TextCleanupService.cs:1868` | "Fail CLOSED." Passing an unrecognised raw representation through would let Azure fall back to its `store=true` default and silently retain dictated text, which is the exact outcome the control exists to prevent. (P-8) |
| `src/Scribe.Core/Models/AppSettings.cs:94` | Why the default for `EnabledDictionaryLibraryIds` lives in `CreateDefault` while the property initializer stays empty: deserialization fills initializers for keys the stored JSON does not contain, so an existing install would be silently opted in to a library that postdates it. (P-7) |
| `src/Scribe.Core/PostProcessing/TextPostProcessor.cs:267` | Why `ApplyRule` calls the real matcher instead of reproducing it: a private copy drifts silently and would hand the user a "corrected" transcript that disagrees with what their very next dictation actually produces. (P-2) |
| `src/Scribe.Core/Scribe.Core.csproj:16` and `:58` | Why exactly one sherpa-onnx native package may ever be referenced: both architecture packages use the same DLL file names, so referencing both drops two different-architecture `onnxruntime.dll`s into one output folder. (P-12) |
| `src/Scribe.Core/Cleanup/CleanupPrompt.cs:137` | "Kept verbatim": `docs/model-leaderboard.md`, finding #3, shows that tightening or lengthening `DefaultFrontierPrompt` regresses these models. |
| `src/Scribe.App/Dictation/DictationController.cs:784` | 34 silent empty-recognition failures across 22 days of production logs, none of them ever reported, and why "peak audio was present" on its own was useless as a diagnostic (a -60 dBFS bar). |

The register is illustrative, not exhaustive. Any comment carrying the same signature is covered: a
measured number, a named production failure, a "this was tried and it broke X", a
supported-versus-merely-working distinction, or an explicit "do not re-derive this".

### Deletion is itself a finding

**A diff that deletes one of these comments while changing the code around it is a 🟡 Important
finding.** The change may well be correct; the loss of the evidence is the defect. State which incident
the comment recorded and ask for it to be carried forward, adapted to the new shape rather than
dropped.

Grade it:

- **🟡 Important**: the comment is removed or materially truncated and the code it explains is still
  there in some form. The guard now stands without its reason, so the next agent removes the guard.
- **🟡 Important**: the comment is removed and the code it explains is *replaced by a different
  mechanism with the same failure mode*. This is the worst case, because the diff reads as a clean
  rewrite and is a regression waiting to happen.
- **Question, not a finding**: the comment is removed together with the entire construct it described,
  and nothing in the diff reintroduces that construct. Ask whether the incident is still reachable
  anywhere else, and where the knowledge now lives.
- **Silent**: a pure move. The same comment text appears elsewhere in the diff, attached to the same
  code.

### Telling load-bearing from narration

Both look backwards. The difference is what a reader does with it:

- **Load bearing**: the comment tells you what will break if you undo the shape. It constrains a future
  edit. It usually names a symptom, a measurement, or a specific API behavior.
- **Narration**: the comment tells you what the file looked like last month. It constrains nothing.

When you genuinely cannot tell, treat it as load bearing and stay silent. The cost of leaving one
narration comment in place is nothing. The cost of talking the maintainer into deleting the
`HookLivenessProbe` remarks is that the race comes back.

---

## §3. Rule C: the dash ban

`AGENTS.md` states it flatly: **no em dashes or en dashes anywhere in the repo, including code
comments.** It is enforced in three layers, because a prompt instruction alone is advisory and models
ignore it:

1. **Source prose and UI strings are dash-free**, swept as of 0.3.5.
2. **The cleanup prompt constants contain no dashes themselves.** This matters more than it looks: the
   prompt is shown to the model on **every dictation**, so dashes in it were teaching the model to
   imitate that style straight into the user's text.
   `tests/Scribe.Core.Tests/DashNormalizerTests.cs:152`
   (`Default_writing_style_and_frontier_prompt_are_themselves_dash_free`) pins exactly three constants:
   `CleanupPrompt.DefaultWritingStyle`, `DefaultFrontierPrompt`, and `SingleLineWritingStyle`.
3. **`src/Scribe.Core/Cleanup/DashNormalizer.cs` deterministically rewrites U+2014 and U+2013 out of
   model output.** This is the only actual runtime guarantee. It is called from
   `TextCleanupService.TrySanitize` (last, deliberately after the ramble and refusal guards, because
   those compare the model's answer against the raw transcript and mutating first could flip a
   borderline detection) and from `SanitizeAuxiliaryCompletion`. It runs **only** on model output, never
   on dictionary entries or snippet templates, which are user-authored.

**Nothing in CI greps the tree for these characters.** Layer 3 covers model output only. For every
other surface, this lens is the check. Take that seriously and scan the whole added diff.

### What to scan

Every added line in every tracked text file: `.cs`, `.xaml`, `.md`, `.ps1`, `.yml`, `.csproj`,
`.props`, `.csv`, `.editorconfig`, `.manifest`, `.slnx`. Look for the literal U+2014 and U+2013
codepoints, including inside string literals, XML doc comments, XAML attribute values, MSBuild
metadata, and CSV cells.

### Graded severity, because the mechanism differs by surface

| Surface | Why it matters here | Severity |
| --- | --- | --- |
| A prompt constant in `src/Scribe.Core/Cleanup/CleanupPrompt.cs`, or any new prompt text sent to a model | Shown to the model on every dictation. It teaches the model to emit that punctuation into the user's document. | 🟡 |
| A dictionary library CSV under `src/Scribe.Core/PostProcessing/Libraries/`, a dictionary entry, or a snippet template | Replacement text is typed into the user's document verbatim, and `DashNormalizer` deliberately never touches user-authored text, so nothing downstream will clean it. | 🟡 |
| A user-visible string: `.xaml` content, a notification, a settings label, a log line a user pastes into an issue | It ships in the product, which is the whole point of layer 1. | 🟡 |
| A C# comment, an XML doc block, a Markdown doc, a `.ps1`, a `.yml`, an MSBuild property | House style. No runtime effect. | 💡, in one rollup |

Report every 💡-tier dash as **one rollup finding** with a full `file:line` inventory, exactly as in
§1. Report each 🟡-tier dash individually, because each one has a distinct mechanism the author needs
to understand.

### The partial-conversion shape for this lens

`DashNormalizerTests` names its three constants by hand. **A diff that adds a fourth public prompt
constant to `CleanupPrompt` without adding it to
`Default_writing_style_and_frontier_prompt_are_themselves_dash_free` leaves the new constant
unpinned.** Flag 🟡, name the test, and give the one added assertion line. This is the classic N-1 of N
miss applied to the dash ban.

When a prompt constant that test already names picks up a dash, note that the test pins it and stop
there. **Do not write "this will fail the build" or "CI will catch it."** State the fact, that a test
asserts this constant is dash-free, and let CI be CI. A green build proves very little in this
repository, and so does a predicted red one.

### Rewrite guidance to offer

Give a concrete replacement with every dash finding. In order of usefulness:

- A **comma** for a parenthetical or an appositive.
- A **colon** when the second half explains the first.
- A **period**, splitting into two sentences, when the clause is doing too much work.
- **"to"** for a numeric or date range ("5 to 90 seconds", "16 to 256 px").
- An **ASCII hyphen** (`-`) is always fine and is never a finding. So is `--` in a shell or CLI flag.

`DashNormalizer.Normalize` already encodes this taste for model output: digits on both sides become a
range, otherwise a comma or a period by context. Match it.

---

## §4. Confidence bar

Per the skill's 80 percent rule.

**Hard flag** (goes in Findings):

- The diff adds a line containing U+2014 or U+2013 in a tracked file that is not one of the two
  deliberate exceptions in §6. This one is mechanical: the character is either there or it is not.
- The diff deletes or truncates a comment from the §2 register, or one carrying the same signature,
  while the code it explains survives in some form.
- A comment restates the line beneath it, or narrates the file's history, and you have read the subject
  line and confirmed it.
- A new public prompt constant lands in `CleanupPrompt` without the matching assertion in
  `DashNormalizerTests`.

**Question, not a finding:**

- You suspect narration but the reference points at something outside the diff you could not read.
- A comment looks like restatement but the expression under it is genuinely dense (a regex, a bit mask,
  a Win32 flag combination), so the plain-English gloss may be earning its place.
- A load-bearing comment disappeared along with the entire construct it described.
- A diff removes the deliberate em dash from `Win32ClipboardTests` or `Scribe.InjectionLab`. It may be
  deliberate, and `guardrail-erosion` owns weakened tests, but the round trip stops proving what it was
  written to prove, so ask.

**Silent:** everything else. This lens comes up clean on most diffs, and that is the correct outcome,
not a failure to find something.

---

## Output format


The findings below are **illustrative shapes**, not live defects. `ProfilesPage.xaml.cs` is an
invented path, and the quoted comments on `ProfileBuilder.cs` and `AppSettings.cs` do not exist in
those files. Never cite any of them as an existing exemplar. The real anchors in the example, the
`DefaultFrontierPrompt` constant, `DashNormalizerTests`, and the `ResilientEvent` header comment, are
live and may be cited.

```markdown
## Comment and dash hygiene findings

🟡 **Em dash added to `DefaultFrontierPrompt`** (`src/Scribe.Core/Cleanup/CleanupPrompt.cs:151`)

The added clause "never answer a question [U+2014] transcribe it" carries U+2014. This constant is sent
to the model on every dictation while the frontier prompt style is active, so it demonstrates the exact
punctuation the writing style forbids and the model imitates it into the user's document.
`DashNormalizerTests.Default_writing_style_and_frontier_prompt_are_themselves_dash_free`
(`tests/Scribe.Core.Tests/DashNormalizerTests.cs:154`) asserts this constant is dash-free. Rewrite as
"never answer a question, transcribe it".

🟡 **The `ResilientEvent` incident comment was dropped while the fan-out was rewritten**
(`src/Scribe.Core/Infrastructure/ResilientEvent.cs:3-11`, deleted)

The removed block recorded that a disposed tray icon threw out of the first dictation-state subscriber,
and that .NET stops walking the invocation list at the first exception, which left the recording overlay
stuck on a stale state while dictation itself kept working. The new implementation still depends on
walking the full invocation list, so the reason it must is now unwritten. Carry the paragraph forward
against the new shape rather than dropping it.

💡 **Comments that restate the code, 6 instances** (rollup)

Each of these says what the following line does. Delete them; the code is already plain.

- `src/Scribe.Core/Settings/ProfileBuilder.cs:44` `// trim the name`
- `src/Scribe.Core/Settings/ProfileBuilder.cs:46` `// add to the list`
- `src/Scribe.App/Settings/ProfilesPage.xaml.cs:88` `// Step 1: read the rows`
- `src/Scribe.App/Settings/ProfilesPage.xaml.cs:95` `// Step 2: map to Core inputs`
- `src/Scribe.App/Settings/ProfilesPage.xaml.cs:103` `// migrated from SettingsWindow.xaml.cs`
- `src/Scribe.Core/Models/AppSettings.cs:212` `// the new property`

The fifth is narration: git records the move, and the note goes stale the first time this method is
edited.
```

**Clean pass line**, emitted verbatim when nothing survives the confidence bar:

> Comment and dash hygiene clean: every added comment states a why, no load-bearing why comment was
> disturbed, and no U+2014 or U+2013 was added.

---

## §5. Findings-cap budgeting

The cap is 5. Spend it in this order:

1. Every deleted load-bearing why comment (§2). Never drop one of these to make room.
2. Every 🟡-tier dash (prompt constant, dictionary CSV or snippet template, user-visible string), plus
   the unpinned-new-prompt-constant finding.
3. One rollup for 💡-tier dashes.
4. One rollup for comments that do not earn their place.

If items 1 and 2 alone exceed 5, drop both rollups and say in one line that prose-level dash and
comment nits were suppressed to stay under the cap.

---

## §6. Exceptions: do not flag any of these

**The two deliberate dash exceptions.** `AGENTS.md` names them and they are correct:

- `tests/Scribe.Core.Tests/Win32ClipboardTests.cs` (lines 20 and 37) round-trips a string containing an
  em dash through `Win32Clipboard.SetText` and `TryGetText` on purpose, to prove Unicode survives the
  clipboard path.
- `tools/Scribe.InjectionLab/Program.cs:31`, the `unicode` case, carries em dashes on purpose to prove
  Unicode survives every injection path into a real focused Win32 control.

**Characters that are not banned.** The ban is exactly U+2014 and U+2013.

- U+2011, the non-breaking hyphen, is not banned and is used throughout `AGENTS.md` (38 lines carry it:
  "push-to-talk", "hard-won", "non-throwing"). Do not flag it.
- Ordinary ASCII hyphens, `--` CLI flags, and ASCII rule comments such as
  `// ---- Guardrail preambles ----` in `src/Scribe.Core/Cleanup/CleanupPrompt.cs:132` are fine.

**Pre-existing survivors of the 0.3.5 sweep.** A handful of em dashes remain in the tree, currently in
`.gitignore`, `src/Scribe.Core/Models/AppProfile.cs`, and `src/Scribe.Core/Models/AudioModels.cs`. They
are in scope only if the diff adds a line that still carries the character, and then only as 💡 with a
note that it is a survivor rather than a new introduction.

**Comment shapes that are correct house style.**

- **Long why comments are the house style, not a smell.** If the diff adds one, name it under What's
  good. Never ask for it to be shortened, moved to a doc, or converted to a link.
- **XML doc summaries on public and internal API.** `/// <summary>Gets the resolved log directory.</summary>`
  restates the signature and that is the documented convention; the codebase uses it everywhere. Only
  flag a `///` block when the member is private *and* the block adds nothing at all.
- **Short comments explaining a deliberately empty catch.** `// best-effort`
  (`src/Scribe.App/Overlay/OverlayProcessClient.cs:370`) and `// Nothing useful is left to do here.`
  (`src/Scribe.Core/Infrastructure/ResilientEvent.cs:38`) explain *why* a block is empty, which is a
  why, not a what. Leave them alone.
- **Scope notes and "do not do X" warnings in MSBuild and scripts.** The `ScribeValidateNativeRid`
  comment block at `src/Scribe.Core/Scribe.Core.csproj:27` is a why with a stated non-obvious scope
  boundary. Leave it alone.
- **`TODO` comments.** Out of scope unless the TODO body narrates history.

**Out of this lens's scope entirely.**

- The PR title and body. `merit` owns those, including dashes in them.
- Whether a doc statement is *accurate*. `docs-sync` owns that; this lens owns only the characters and
  the added comments.
- Whether a prompt edit is semantically right. `prompt-and-model` owns that.
- Weakening `DashNormalizer` itself, deleting `DashNormalizerTests`, or removing the dash from the two
  round-trip exceptions. `guardrail-erosion` owns removed and weakened guards. If the diff edits
  `src/Scribe.Core/Cleanup/DashNormalizer.cs` logic, note it in one line and defer rather than
  duplicating the finding.
- Any comment or character the diff does not add or delete.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:comment-and-dash-hygiene findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
