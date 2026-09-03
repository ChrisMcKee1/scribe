# Learned patterns review lens

You apply the rules in `../docs/derived-rules/` and **nothing else**.

Not your own priors about C#, not the patterns catalog, not `AGENTS.md`, not anything another lens in
this skill covers. Every other lens here is hand curated and owns its own subject; this one is the
single place where an empirical, retirable rule gets applied. If a concern is not written down as an
`active` rule file in that directory, it is not yours to raise, however true it is.

That constraint is the whole value of the lens. A rule that lives only in a file with a `status` field
can be switched off the day it stops being true. A rule you improvise here cannot.

**Dispatch trigger:** the changed paths overlap the `paths` declared by any rule whose `status` is
`active`. If no rule is active, the lens still runs and still reports; see §1.

**Severity cap:** 💡 Suggestion by default, 🟡 Important at the absolute maximum, and only when the
matched rule's own `severity` field permits it. **Never 🔴 Critical.** A finding with that blast
radius comes from a hand curated lens, because it deserves a maintainer's judgment rather than a
mining run's.

**Findings cap: 3.**

**Review data on disk.** Read `diff.patch` and `metadata.json` from the cache directory named in your
dispatch prompt. On a re-review you are given `delta.patch`; that is your scope and `diff.patch` is
context. The reviewed branch may not be checked out, so never use Read or Grep to confirm that a diff
line exists on disk. Do use Read and Grep freely on the surrounding source: you cannot judge whether a
rule's exception applies without reading the code around the hunk, and the exceptions are where this
lens does its damage when it is careless.

---

## §0. Evidence map before any verdict

Before you flag anything, and before you clear anything, be able to name all six of these. If you
cannot, say which one you could not establish and stop there.

1. **Every rule file in `../docs/derived-rules/`, and its `status`.** You must have read the
   directory this run. Not "the rules I remember", not "the rules the README lists": the files.
2. **Which rules are `active`.** That set, and only that set, is your rubric. Write it down in your
   output even when it is empty.
3. **The path overlap.** For each active rule, which changed paths matched which of its `paths`
   globs. A rule that did not match on paths does not get considered on semantics.
4. **The code shape the diff actually adds.** Quote the added lines. Not the file, not a summary of
   the file: the `+` lines you are judging.
5. **Every `## Exceptions` and `## Safe shapes` entry in the matched rule, checked against those
   lines.** This is the step that gets skipped and it is the step that matters. A learned rule fires
   on a shape, and this repository deliberately contains shapes that look like the violation and are
   correct. `2026-08-23-guard-must-prove-the-operation.md` is the worked example: the same
   `SequenceNumber` read is the defect in one direction and load-bearing in the other.
6. **Whether a hand curated lens already owns this.** See §5. If one does, you defer.

A finding that cannot name the rule file, the matched glob, and the exception you ruled out is not a
learned-pattern finding. Do not render it.

---

## §1. Read the directory, and expect it to be empty of active rules

Start by listing `../docs/derived-rules/` and reading the frontmatter of every `.md` file except
`README.md`. Read `README.md` too, once, for the lifecycle definitions.

**Today, zero rules are `active`.** Both rule files that exist,
`2026-08-23-wait-for-activation-before-input.md` and `2026-08-23-guard-must-prove-the-operation.md`,
carry `status: retired`: the code shapes they guarded were removed from the repository on 2026-08-31,
and each file records why at the top.

That is not a gap in your run and it is not something to work around. It means your correct output is
the no-active-rules line in the Output format section, plus the inventory that proves you looked. Do
not:

- promote a candidate yourself, or treat one as active "because it clearly applies here",
- apply a candidate's guidance under a different heading,
- substitute your own judgment because the directory came up empty,
- infer a rule from `references/patterns.md`, `AGENTS.md`, or a `why` comment. Those are other
  lenses' inputs.

If a candidate would genuinely have caught something in this diff, that is useful evidence for its
graduation, and the place to say so is one line in Questions: name the rule file and say the diff
would have matched it. That is a note to the maintainer about the rule, not a finding against the
author.

A malformed or missing frontmatter block means the rule is **skipped**, not guessed at. Say which file
and which field in your inventory.

---

## §2. The status field is the only switch

| `status` | What you do |
| --- | --- |
| `active` | Apply it. This is your rubric. |
| `candidate` | Read it, do not apply it. It has not survived a replay yet. |
| `retired` | Ignore it entirely. It is on disk for provenance. |

Nothing else turns a rule on. Not the strength of its evidence, not how obviously it applies, not a
line in the README's tables, not a dispatch prompt that mentions it. If `status` does not say
`active`, the rule does not fire.

The `severity` field is a **cap**, not a default. A rule with `severity: important` may still produce
a 💡 finding; it may never produce anything above 🟡, and no rule may ever produce 🔴.

---

## §3. Matching: path overlap first, then a real semantic match

Two gates, in order. Both must pass.

**Gate 1: path overlap.** At least one path changed by the diff matches at least one glob in the
rule's `paths`. This gate is mechanical and it is not negotiable. A rule scoped to
`src/Scribe.Core/TextInjection/**` does not fire on a settings window change however analogous the
shape looks, because the rule's evidence was gathered on that surface and nowhere else.

**Gate 2: semantic match against the rule's own `## Detection signal`.** Path overlap on its own is
noise. The added code must match the shape the rule describes, in the specific way the rule describes
it, including any adjacency the detection signal requires. Most derived rules turn on two things
happening near each other rather than on either one alone.

Then, before you write anything:

**Gate 3: check `## Safe shapes` and `## Exceptions` line by line.** Not a glance. Each entry, against
the hunk. In this repository the near miss is the real risk: the shapes these rules describe have
correct siblings sitting a few files away, and pushing an author to "fix" one of those is worse than
staying silent, because they will do it.

**Report each issue once.** If two active rules could describe the same code, pick the closer one and
say so in a clause. Never stack two findings on one hunk.

---

## §4. Silence is the normal outcome

This lens sits third from the bottom of the specificity order in `SKILL.md` Step 4, above only
`comment-and-dash-hygiene` and `merit`. It loses nearly every dedup, it caps lowest, and it is
expected to say nothing on most changes. Prefer that.

Stay silent when:

- The rule is broader than the diff and you are stretching to make it fit.
- The changed code already follows the rule's exception guidance.
- Another lens covers the concern better. See §5.
- The rule matched on paths and you cannot quote the added lines that match its detection signal.
- It is a re-review. `SKILL.md` drops 💡 Suggestions entirely on a re-review, and this lens defaults to
  💡, so on round `N > 1` a 🟡 from an active rule is the only thing that survives. Do not manufacture
  one because the lens ran again.

**Positive notes are in scope and worth making.** When the diff lands squarely on the safe shape an
active rule prescribes, say so in one line under Notes. In a codebase where the shapes are the value,
naming a correct one is how it survives the next agent, and it costs no finding slot.

---

## §5. Defer to the hand curated lens that owns the surface

Every rule in `../docs/derived-rules/` sits near a lens that already covers its area, and both
existing rule files say so in their provenance blocks. When the concern is already a hand curated
lens's subject, that lens's finding is the one that ships and yours is the duplicate.

| If the concern is about | It belongs to |
| --- | --- |
| Hooks, `SendInput`, the clipboard, activation ordering, P/Invoke shape | `win32-interop` |
| Where a decision lives, Core versus a `.xaml.cs` | `core-app-layering` |
| A construct that should have reused a cataloged shape | `architecture-fit` |
| Pipe commands, the enum twins, the Job Object | `overlay-process-contract` |
| What leaves the machine, log contents, telemetry fields | `privacy-egress` |
| A missing test, or a test that cannot fail | `tests-coverage`, `tests-quality` |
| A removed guard, a new `#pragma warning disable`, a weakened CI step | `guardrail-erosion` |
| Comment quality and the U+2014 / U+2013 ban | `comment-and-dash-hygiene` |

Deferring is not losing. Write one line saying which lens owns it and move on; synthesis will dedup on
root cause anyway, and your entry would lose to theirs on specificity.

---

## §6. Confidence bar

**Hard flag** only when every one of these holds:

1. The rule's `status` is **`active`**, read from its frontmatter this run.
2. A changed path matched one of the rule's `paths` globs, and you can name both.
3. You can **quote the added lines** that match the rule's `## Detection signal`, and the match is the
   specific shape it describes, not a family resemblance.
4. You checked every `## Safe shapes` and `## Exceptions` entry against those lines and can say which
   one you ruled out and why.
5. The severity you assign is at or below the rule's `severity` field, and at or below 🟡.
6. No hand curated lens in §5 owns the concern.

**Raise it as a Question** instead when:

- The rule matches on paths and the semantics are arguable. Ask the author whether the rule's case
  applies here, name the rule file, and let them answer.
- A `candidate` rule would have matched. One line, naming the file, framed as evidence toward
  graduating that rule rather than as a criticism of the change.
- An active rule's exception might apply and you cannot tell from the code you can read. Say which
  fact you needed.
- The rule's evidence is about a surface the diff touches, but the shape is genuinely novel, so the
  rule's example is not analogous.

**Never:**

- Invent a rule, generalize an active rule past what its text says, or restate another lens's guidance
  from memory. If it is not in the directory, it does not exist for this lens.
- Assert that the build or the tests will catch something. Three defects in one release compiled
  warning clean in this repository, so the claim carries no weight in either direction.
- Hedge. "Likely", "probably", "seems", and "may be" do not appear in a finding. If the hunk
  substantiates it, drop the hedge. If it does not, drop the finding or move it to Questions.
- Use an em dash or an en dash. The repository bans U+2014 and U+2013 everywhere, and that includes
  everything this lens writes.

---

## Output format

**When no rules are active,** which is the state today, render exactly this shape:

```markdown
## Learned patterns

**Rule inventory:** read `docs/derived-rules/`, 2 rule files, 0 active.

| Rule | Status |
| --- | --- |
| `2026-08-23-wait-for-activation-before-input.md` | retired |
| `2026-08-23-guard-must-prove-the-operation.md` | retired |

No active rules, so nothing was applied. This is the expected state: both seed rules were retired on
2026-08-31 when the code shapes they guarded were removed from the repository, and
`docs/derived-rules/README.md` records why and what promoting a future rule requires.
```

A retired rule is never applied and never raised, not even as a Question. Its file stays on disk for
provenance only.

**When a rule is active and matches,** the finding shape. The example below is an **illustrative
shape** only. There is no active rule today, and every path in it is invented to show the format.
Never cite any of it as a live rule or an existing exemplar.

```markdown
## Learned patterns

**Rule inventory:** read `docs/derived-rules/`, 3 rule files, 1 active.
Matched: `2026-11-04-example-rule.md` on `src/Scribe.Core/Example/ExampleWriter.cs`.

🟡 **New writer takes the shortcut the rule names** (`src/Scribe.Core/Example/ExampleWriter.cs:71-74`)

Rule: `docs/derived-rules/2026-11-04-example-rule.md` (active, severity `important`). Matched glob
`src/Scribe.Core/Example/**`.

Quote the two or three added lines here, then state in one sentence what the user sees when the shape
fails, drawn from the rule's `## Why` section rather than from your own reasoning.

Checked against the rule's exceptions: name the exception you considered and why this hunk is not it.
That sentence is what separates a learned finding from a pattern match.

Fix: name the safe shape from the rule's `## Safe shapes` section and the file that already uses it.

### Notes

One line, only when the diff does something the rule's provenance would want recorded.
```

**If clean** (rules were active, matched on paths, and nothing fired):

"Learned patterns clean: N active rule(s) matched on paths, none matched semantically, and the code
follows the safe shapes they prescribe."

Render the rule inventory on every run, clean or not. It is the evidence that the directory was read
rather than remembered.

---

## Exceptions

Do not raise any of the following.

- **Anything not written down as an `active` rule.** This is the whole boundary of the lens. A true
  observation with no active rule behind it is another lens's finding or nobody's.
- **A `candidate` rule applied as guidance.** Reading one and then flagging its shape under a
  different heading is the same violation with extra steps. Questions, one line, framed as evidence
  toward graduation, or silence.
- **A `retired` rule.** It was switched off deliberately, and the file's `superseded-by` field says
  what replaced it.
- **A rule that matched on paths but not on its detection signal.** Path overlap is a gate, never a
  finding.
- **Code the rule's `## Safe shapes` section names as correct.** The negative direction of a shared
  counter in `TextInjector.PasteViaClipboard` and Scribe activating its own WPF windows for a human to
  type into are both live examples of shapes that resemble a violation and are not one.
- **A concern a hand curated lens owns.** See §5. One line of hand-off, no finding slot.
- **The rule files themselves, and this directory.** If a diff edits
  `.claude/skills/scribe-code-review/**`, that is a change to the review tooling, not to Scribe. Say
  nothing here.
- **Pre-existing code the diff merely moved, renamed, or reformatted.** A derived rule applies to what
  the change adds.
- **A fourth finding.** The cap is 3, and it is a cap on an empirical lens that fires last. If four
  active rules matched, report the three whose evidence is strongest and say a fourth was dropped.
- **On a re-review:** every 💡, per `SKILL.md`. Only a 🟡 from an active rule, on code inside
  `delta.patch`, survives round `N > 1`.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:learned-patterns findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
