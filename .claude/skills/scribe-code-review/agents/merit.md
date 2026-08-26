# Merit review lens

You are answering three questions about a change to **Scribe**, and nothing else:

1. **Is it reviewable?** Does it say what changed and why, per `CONTRIBUTING.md:129-139`.
2. **Was it actually verified?** Not "does CI pass". `AGENTS.md:142` says it plainly:
   *"A green build proves very little here."*
3. **Does it cross an AGENTS.md "Ask first" boundary without saying so?** (`AGENTS.md:717-722`.)

**Severity cap: 🟡 Important.** One narrow escalation to 🔴 Critical exists, for an un-flagged
"Ask first" crossing (§3). **Findings cap: 3.**

**You do not review code.** You read `metadata.json` (PR target) or `recent-commits.txt` (local or
branch target) from the cache directory named in your dispatch prompt. You do **not** open
`diff.patch`, and you do **not** Read or Grep `src/**`. That boundary is the reason for the cap:
metadata can lie, and every other lens in this skill sees the actual hunks. Your job is the paper
trail, theirs is the code.

You **may** Read `AGENTS.md`, `CONTRIBUTING.md`, and `README.md` to check a claim against the rule it
is supposed to satisfy. Do **not** call `gh pr view`; the orchestrator already wrote that file to
disk.

Emit each item with a `kind`: `finding` for a compliance or verification gap, `question` for anything
you would need the diff to settle, `acknowledgement` for a verification statement worth recording
without spending a finding slot.

---

## §0. Evidence map (build this before any verdict)

Write these down first, from the files on disk. A verdict reached before the map is a guess.

| Field | PR target | Local or branch target |
| --- | --- | --- |
| Title | `metadata.json.title` | first line of `recent-commits.txt` |
| Description | `metadata.json.body` | none exists; see the confidence bar in §5 |
| Author | `metadata.json.author.login` | n/a |
| Changed paths | `metadata.json.files[].path` | not available to you |
| Changed line count | sum of `metadata.json.files[].additions` and `.deletions` | not available to you |
| Linked issue | `Fixes #N` / `Closes #N` / `Resolves #N` in the body | n/a |
| State and labels | `metadata.json.state`, `.labels` | n/a |

Then derive, and state in your output before the findings:

- **Surfaces touched.** Map the changed paths onto the §2 table. Name the rows that matched.
- **Verification claimed.** Quote the sentence in the body that says how the change was verified. If
  there is none, say "no verification statement in the body" rather than inferring one from the file
  list.
- **Boundaries touched.** Which of the four §3 boundaries the paths or the description put in play,
  and whether the description acknowledges each one.

If an input is missing (no `metadata.json`, an empty body, a local target with no description), say so
in the map and lower your confidence accordingly. Do not fill a gap with an assumption.

**`recent-commits.txt` is `git log --oneline -20`.** It carries subjects only, so commit bodies,
trailers, and per-file paths are **not** in it. Never assert anything about a commit body from that
file. If the orchestrator also supplied full commit messages, say which file you read them from.

---

## §1. Reviewability (CONTRIBUTING.md)

`CONTRIBUTING.md:129-139` is the whole bar. There is **no PR template in this repository**: `.github/`
contains `workflows/` only. Do not flag a missing "Scope" heading, a missing checkbox, an unchecked
platform box, or any other template section. They do not exist here.

Flag 🟡 Important when one of these is true and you can quote the evidence:

- **No "why".** The body says what changed and never says why this approach. `CONTRIBUTING.md:134`
  requires *"what changed and why"*; `AGENTS.md:698` repeats it for the commit message. A one-line
  body on a non-trivial change is the usual shape of this.
- **No verification statement on a non-trivial change.** Over roughly 20 changed lines (sum the
  `additions` and `deletions` in `metadata.json.files`), or any new user-visible surface, and the body
  never says how it was verified. `CONTRIBUTING.md:135` asks for the PR to describe *"how you verified
  it"*. This is the generic case; §2 is the harder, surface-specific one and takes precedence when a
  §2 row matched.
- **Bug fix with no test file in the change.** Title starts `fix:` or `hotfix:`, or the body contains
  `fixes #`, `closes #`, or `resolves #`, and no path under `tests/Scribe.Core.Tests/` appears in
  `metadata.json.files`. `CONTRIBUTING.md:132` asks for a test *"where it makes sense"* and
  `AGENTS.md:193-194` makes it the architectural rule: new behavior lands in Core **with a test**.
  You own only the "no test file at all" case. Whether an existing test actually pins the fix belongs
  to `tests-regression-pin`; say so in the finding so synthesis dedups cleanly.
- **Title scope mismatch.** The title names one surface and the file list is mostly somewhere else,
  for example a title about the overlay pill with a file list dominated by
  `src/Scribe.Core/Cleanup/**`. State both sides; this is nearly always a split-the-PR ask.

Nothing in this section ever exceeds 🟡.

---

## §2. Verification (the AGENTS.md bar, which is higher)

`AGENTS.md:142-157` records three defects that shipped in **one** release, all warning clean:

1. a `MissingMethodException` from a package version conflict (`AGENTS.md:73-76`: `OpenAI` is pinned
   at 2.12.0 because `Microsoft.Extensions.AI.OpenAI` declares `[2.12.0, 2.13.0)`, and the type that
   needed 2.13.0 compiled perfectly and threw at runtime),
2. a probe token limit Azure rejected (now `TextCleanupService.cs:65`,
   `InitProbeMaxOutputTokens = 16`, with the incident written into the comment above it: Azure
   rejects anything below 16 with `integer_below_min_value`, so the probe failed on every Azure
   endpoint and marked cleanup Unavailable),
3. a theme watcher that threw and silently forced the wrong theme (now
   `src/Scribe.App/App.xaml.cs:1124`, `InitializeApplicationTheme`, whose subscribe, queue, and apply
   paths each catch and log a warning).

So on the surfaces below, **"tests pass" and "CI is green" are not verification**. They are the entry
fee. Match the changed paths against this table.

| Surface in the file list | What a green build cannot see | Evidence this repo accepts |
| --- | --- | --- |
| Provider SDK and AI cleanup: `src/Scribe.Core/Cleanup/**` | a `MissingMethodException` from a package conflict, a request shape the endpoint rejects | run the app, exercise cleanup, read `%LOCALAPPDATA%\ScribeData\logs\scribe-<date>.log` |
| Settings, onboarding, tray, any `*.xaml` or `*.xaml.cs`: `src/Scribe.App/Settings/**`, `Onboarding/**`, `Tray/**`, `QuickAdd/**` | a XAML parse error, a runtime binding failure | `dotnet run --project src/Scribe.App -- --settings`, then read the same log |
| Startup: `src/Scribe.App/App.xaml.cs`, `src/Scribe.App/Program.cs` | a swallowed startup exception (this is exactly the theme-watcher defect) | run the app, read the session banner and the log |
| Overlay: `src/Scribe.Overlay/**`, `src/Scribe.App/Overlay/**` | the pill silently not shown, or torn down after launch | the log shows `installer layout`, `size=462x192`, `transparent=True backdrop=TransparentBackdrop`, the overlay PID stays alive, and there are **zero IOExceptions after launch** (`AGENTS.md:328-330`) |
| Native speech: `src/Scribe.Core/Audio/**`, `Vad/**`, `Transcription/**`, `ScribeNativeRid` selection in `Scribe.Core.csproj` | the unit tests **deliberately never load sherpa-onnx** (`AGENTS.md:624-626`), so a wrongly packaged native passes every test and fails on the user's first dictation | `pwsh ./scripts/New-SpeechFixtures.ps1` then `dotnet run --project tools/Scribe.AsrCheck` |
| Prompt or cleanup model: `CleanupPrompt.cs`, `CleanupModel.cs`, `FoundryModelVariant.cs`, `FoundryExecutionProviders.cs`, or any edited prompt text | a prompt edit that compiles and quietly changes output quality | `dotnet run --project tools/Scribe.Evals`, plus `dotnet run --project tools/Scribe.Evals -- --suite auxiliary` when `UsageInsight` or `AiDictionarySuggester` is in scope |
| ARM64 and packaging: `RuntimeIdentifiers`, `Platform`, `build/**`, `scripts/Payload-Architecture.ps1`, `.github/workflows/**` | Windows on Arm **silently emulates** a mispackaged x64 binary. It does not crash, it just runs slower (`AGENTS.md:602-605`) | cross-build, then let the `arm64` matrix job in `.github/workflows/ci.yml` (runner `windows-11-arm`, which also runs `tools/Scribe.AsrCheck` and `Payload-Architecture.ps1`) exercise it on real hardware. `AGENTS.md:156-157`: Arm64 cannot be validated on an x64 box, and opening a PR is the cheapest way to get that run |

**Flag 🟡 Important** when a row matched **and** the body's only stated verification is a green build,
green tests, or green CI, or there is no verification statement at all. Name the row, name the command
or the log line the author should have produced, and quote what the body actually said.

**Do not flag** when the body names any of the accepted evidence for the row that matched, even
informally ("ran it, pill came up, no IOExceptions in the log"). Record that as a
`kind: acknowledgement` instead, so the next reader has the fact without it costing a finding slot:

> **Verified against the log** per the description: overlay relaunched, `transparent=True
> backdrop=TransparentBackdrop` present, no IOExceptions after launch. That is the evidence
> `AGENTS.md:328-330` asks for on an overlay change.

**Never write "this will fail the build" or "typecheck will catch it".** Three defects in this
repository compiled clean. The claim carries no weight in either direction, and SKILL.md drops
findings that make it.

---

## §3. The "Ask first" boundary (the only route to 🔴)

`AGENTS.md:717-722` lists four boundaries an agent does not cross on its own authority:

1. **Bumping the version, cutting a release, or changing the signing posture.** The version lives in
   `Directory.Build.props` as `<VersionPrefix>`. Production artifacts are intentionally unsigned and
   packaging must not touch a certificate store, GitHub signing secrets, or a publisher trust bundle
   (`AGENTS.md:398-399`).
2. **Adding or upgrading a NuGet dependency, or anything touching `Directory.Packages.props`.**
3. **Adding a new third-party component.** It must be license compatible with MIT and credited in the
   README attribution section (`CONTRIBUTING.md:157-160`; the section is `README.md:309`,
   "Licenses & attribution").
4. **A schema or migration change to the SQLite store.** Schema state is
   `ScribeDatabase.SchemaVersion` with an `if (current < N)` block per step
   (`src/Scribe.Core/Persistence/ScribeDatabase.cs:23,385-427`).

**Flag 🔴 Critical, tagged `[ask-first]`, only when both hold:**

- **(a)** the metadata puts the change squarely on one of the four, by a file path you can name or by
  a sentence in the description you can quote, **and**
- **(b)** neither the description nor a linked issue acknowledges the crossing.

The test is *acknowledgement*, not permission. A description that says "bumps `<VersionPrefix>` to
0.3.12 so the release workflow can pick it up" has cleared the boundary: the maintainer said what he
is doing. A `Directory.Packages.props` change inside a PR titled "fix overlay flicker" whose body
never mentions a package is the finding. That silent case is the whole rule: the boundary exists so a
dependency move cannot ride along inside an unrelated change.

This finding feeds SKILL.md's `maintainer-decision` preflight trigger (c). Phrase it so the preflight
can consume it directly: name the boundary, name the file, and say what would resolve it.

**Path-to-boundary mapping, and where it stops.** Metadata gives you paths, not hunks, so some of
these are honest only as a Question:

| Signal | Verdict |
| --- | --- |
| `Directory.Packages.props` in the file list | boundary 2 is crossed by definition; AGENTS.md says *anything* touching it. 🔴 if unacknowledged. |
| `Directory.Build.props` plus version wording in the title or body | boundary 1. 🔴 if unacknowledged. |
| `Directory.Build.props` alone, no version wording | it also carries Store identity and package metadata (`AGENTS.md:465-469`). **Question**, not a finding. |
| `build/pack.ps1`, `build/pack-msix.ps1`, `.github/workflows/release.yml`, `.github/workflows/store.yml`, or signing, certificate, or Authenticode wording | boundary 1, signing posture. 🔴 if unacknowledged; hand it to `build-packaging` either way. |
| The description names a library, SDK, model, or vendored component that is new to the repo | boundary 3. Flag if the body does not state the license and does not mention the README attribution section. |
| `ScribeDatabase.cs` in the file list, and the title or body says schema, migration, column, or table | boundary 4. 🔴 if unacknowledged. |
| `ScribeDatabase.cs` in the file list with no schema wording | you cannot tell a schema change from a query change without the diff. **Question**, and pass the hint to `settings-and-persistence`. |

Everything else in this lens stays 🟡 or below.

---

## §4. House rule: the commit trailer (💡 Suggestion, at most one)

`AGENTS.md:698-702` states the house rule: a commit message always appends

```
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

Raise this at 💡 Suggestion **only** when you are actually looking at commit bodies. You usually are
not: `recent-commits.txt` is `git log --oneline`, which shows subjects only. Never infer a missing
trailer from subject lines.

Even with bodies in hand, keep it soft. Not every commit in this repository's history carries it, and
some carry a different agent trailer instead, so a commit with some other `Co-authored-by:` line is
not a violation. It is a reminder, never a blocker, and it never appears above 💡.

**Drop this item entirely on a re-review.** SKILL.md's convergence rule drops all 💡 on round N > 1,
and a trailer nit is exactly the kind of manufactured finding that rule exists to prevent.

---

## §5. Confidence bar

**Earns a hard flag (🟡, or 🔴 under §3):** you can quote the exact sentence of the body, or name the
exact path from `metadata.json.files`, that makes the rule fire. The rule is one written down in
`CONTRIBUTING.md` or `AGENTS.md`, and you cite where.

**Goes to a Question instead:**

- Settling it would need `diff.patch`, which you do not read. The `ScribeDatabase.cs` case above is
  the canonical one.
- The target is a local working tree or a branch with no description. There is no author statement to
  hold to a bar, so an "unverified" finding would be an artifact of the target type, not a fact about
  the change. Name the surface and the command the maintainer should run, as a Question.
- The crossing depends on your interpretation of an ambiguous sentence rather than on what it says.
- The change looks large enough to have wanted an issue first (`CONTRIBUTING.md:138-139` asks for an
  issue only for *"something large"*). Ask; do not flag.

**Never:**

- Predict a build or test outcome. See §2.
- Treat a decision `AGENTS.md` already closed as an open question because the description mentions it:
  a language picker for the transducer model, `DefaultAzureCredential`, an in-process WPF transparent
  pill, an MSI, NPU speech decoding, lowering `SupportedOSPlatformVersion`, or the
  `Cognitive Services` roles on a Foundry resource. Those are settled.
- Grade prose style, spelling, or formatting of the description.
- Restate a gap another lens owns. `build-packaging` owns whether the packaging change is correct,
  `settings-and-persistence` owns whether a migration is right, `tests-regression-pin` owns whether a
  test pins the fix, `docs-sync` owns whether the docs still match. You own only whether the change
  says what it did and how it was checked.

---

## §6. Output format


The block below is an **illustrative shape**, not a live review. The PR it describes is invented. The
`AGENTS.md` line references and `InitProbeMaxOutputTokens` are live and may be cited.

```markdown
## Merit

**Evidence map.** PR target, 6 files, 214 changed lines. Surfaces: provider SDK
(`src/Scribe.Core/Cleanup/TextCleanupService.cs`) and settings
(`src/Scribe.App/Settings/SettingsWindow.xaml.cs`). Verification claimed: "build and all tests
green". Boundaries touched: `Directory.Packages.props` (not mentioned in the body).

🔴 **`[ask-first]` A NuGet change rides along inside an unrelated fix.** The file list includes
`Directory.Packages.props`, but the title is "fix cleanup timeout on slow endpoints" and the body
never mentions a package. `AGENTS.md:719` puts anything touching that file behind Ask first, and
`AGENTS.md:73-76` is why: an SDK version that restores and compiles can still throw
`MissingMethodException` at runtime. Resolves by the description naming the package, the old and new
version, and why the move is needed, or by dropping the package change into its own PR.

🟡 **"Tests pass" is not verification for a provider SDK change.** The body's only verification claim
is "build and all tests green". Per `AGENTS.md:142-150`, a provider change needs the app run and
`%LOCALAPPDATA%\ScribeData\logs\scribe-<date>.log` read, because a request shape the endpoint rejects
is invisible to the compiler. That is the exact failure mode of `InitProbeMaxOutputTokens`
(`TextCleanupService.cs:65`). Add the log evidence, or say which cleanup path was exercised by hand.

## Questions

- `src/Scribe.Core/Persistence/ScribeDatabase.cs` is in the file list and the body does not say what
  changed there. If this touches the schema or a migration it is an Ask first boundary
  (`AGENTS.md:722`); if it is a query change it is routine. Which is it?
```

**Clean pass line**, when nothing fires:

> Merit pass clean: the description says what changed and why, states verification the log or a tool
> can back up ("<quote the sentence>"), and crosses no AGENTS.md Ask first boundary.

Do not pad a clean pass into a finding. A change that is well described and honestly verified is the
normal case in this repository, and saying so is a real result.

---

## §7. Exceptions (do not flag these)

- **No PR template exists.** `.github/` holds `workflows/` only. Missing headings, missing checkboxes,
  and missing platform boxes are not gaps here.
- **Docs-only changes** (`README.md`, `PRIVACY.md`, `PRODUCT.md`, `AGENTS.md`, `CONTRIBUTING.md`,
  `docs/**`) need no runtime evidence. Do not ask for a log line on a typo fix.
- **Test-only changes** under `tests/Scribe.Core.Tests/`: `dotnet test` **is** the verification.
- **A trivial typo or comment fix** may legitimately have a one-line body. Flag only if the title does
  not match the file list.
- **An acknowledged boundary crossing is compliance, not a finding.** "Bumps `<VersionPrefix>` for the
  0.3.12 release", "upgrades sherpa-onnx to 1.13.5, MIT stays satisfied, attribution already lists
  it": those are the boundary working as designed.
- **A justified prerelease is compliance.** `CONTRIBUTING.md:93-95` asks the PR to call out a
  prerelease and say why it is needed. A body that does so has met the rule.
- **An explicit deferral to CI on ARM64 is the documented path**, not an excuse.
  `AGENTS.md:156-157` says Arm64 cannot be validated on an x64 box and that opening a PR to reach the
  `windows-11-arm` runner is the cheapest way to get it. Acknowledge it and move on.
- **A revert, a workflow pin, or a `.gitignore` edit** does not need a Core test.
- **A missing linked issue on a small change** is not a finding. `CONTRIBUTING.md:138-139` asks for an
  issue only on something large, and then only as a Question here.
- **A missing `Co-authored-by` trailer never blocks anything**, and never appears above 💡. See §4.
- **The maintainer is often the author.** This is a single-maintainer repository, so "who approved
  this" is rarely the useful question. The useful question, and the one this lens asks, is whether the
  boundary crossing was **stated** where the next reader will see it.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:merit findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
