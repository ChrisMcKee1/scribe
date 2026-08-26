# Derived review rules

Empirical review rules for Scribe. This directory is the **single source of truth** for
[`agents/learned-patterns.md`](../../agents/learned-patterns.md), which is the only lens allowed to
apply them. Every other lens in `agents/` stays hand curated.

Two properties make this split worth the ceremony:

- **A rule here can be retired.** The moment an active rule is copied into a hand written lens prompt
  it becomes permanent: nothing tracks it, nothing can turn it off, and a rule that stops being true
  keeps firing forever. `SKILL.md` states the same thing under Review surfaces. Do not fold an active
  rule back into a core lens.
- **A rule here carries its evidence.** A hand curated lens asserts a rule because a maintainer
  decided it. A derived rule has to name the defects that produced it, so anyone can check whether
  the argument still holds.

---

## Where these rules come from

Ordinarily these are mined from a repository's own review history: recurring human review comments
that no existing lens detects. **Scribe has no mined review-comment history yet.** The repository is
small, most review has happened in person or inside a session rather than as PR comments, and no
mining run has been performed.

So the directory starts from the other legitimate source: **defects this project actually shipped or
caught, whose fix is recorded in a `why` comment in the code.** That is the same standard `AGENTS.md`
holds itself to. A rule seeded this way still has to clear the activation bar below before it fires
in a live review, and the bar for that path is stricter, because one defect is an anecdote rather
than a pattern.

When a mining run does happen, it goes in the same directory under the same rules.

---

## Status model

| Status | Meaning | Does `learned-patterns` apply it? |
| --- | --- | --- |
| `candidate` | Evidence backed, written down, not trusted in a live review yet | **No** |
| `active` | Applied by `agents/learned-patterns.md` on every matching diff | **Yes** |
| `retired` | Kept on disk for provenance, deliberately switched off | **No** |

A candidate is not a weak rule. It is a rule whose false-positive behavior has not been tested yet,
and firing it at an author before that is how a review skill loses their trust.

**There are zero active rules today.** Both seed rules below are `candidate`. That means
`agents/learned-patterns.md` currently has nothing to apply and its correct output on every run is
the no-active-rules line. That is the intended state, not a gap to fill by promoting something early.

---

## File naming

```
YYYY-MM-DD-short-kebab-slug.md
```

- The date is when the rule was **written**, not when it was activated. It never changes, so the
  filename stays a stable citation in review output and in these tables.
- The slug names the rule, not the symptom: `wait-for-activation-before-input`, not
  `first-character-missing`. A symptom slug ages badly once the rule generalizes.
- `README.md` is the only file here that is not a rule.

---

## Required frontmatter

Every rule file opens with a YAML frontmatter block. The lens reads `status` and `paths` to decide
whether the rule fires at all, so a missing or malformed block means the rule is skipped, not
guessed at.

```yaml
---
status: candidate          # candidate | active | retired
added: 2026-08-23          # ISO date, matches the filename prefix
surface: text-injection    # short label for the area, used in the tables below
severity: suggestion       # suggestion | important. Never critical. This is the cap.
paths:                     # globs. Path overlap with the diff is the dispatch trigger.
  - src/Scribe.Core/TextInjection/**
  - src/Scribe.App/TextActions/**
evidence:                  # what produced the rule. Concrete and checkable, one entry per source.
  - "src/Scribe.App/TextActions/TextActionController.cs:286 records the defect: ..."
  - "src/Scribe.Core/TextInjection/SelectionReader.cs:180 records the same defect on the read side."
---
```

Field rules:

- **`status`** is required and is the only switch. Nothing else turns a rule on or off.
- **`added`** is required and must match the filename prefix.
- **`activated`** and **`retired`** are required once the status reaches that value, as ISO dates.
- **`surface`** is required. Keep it to one hyphenated word or two.
- **`severity`** is required and is a **cap**, not a default. `agents/learned-patterns.md` may report
  a finding below this severity and never above it. `critical` is not a legal value here: a critical
  finding comes from a hand curated lens, because a rule with a blast radius that large deserves a
  maintainer's judgment rather than a mining run's.
- **`paths`** is required and must be non-empty. These are the globs the lens matches against the
  changed paths. A rule with no path filter fires on everything and will be noise.
- **`evidence`** is required and must be non-empty. Each entry names a file, a line, a PR, or a
  review comment, and states what it shows. "Common sense" and "best practice" are not evidence; if
  that is all there is, the rule belongs in a hand curated lens or nowhere.
- **`supersedes`** and **`superseded-by`** are optional filename references, required whenever a rule
  is retired in favor of another.

---

## Required body sections

Written in this order, and kept short enough to read during a review:

1. **`# Title`**, a sentence-shaped statement of the rule.
2. **`## Guideline`**, what the code must do. Imperative, one paragraph.
3. **`## Why`**, the defect. Name the failure the user saw, not the abstraction.
4. **`## Detection signal`**, what to look for in a diff. Concrete: symbol names, call shapes, the
   adjacency that matters. This is the section that decides false positives.
5. **`## Safe shapes`** (optional but strongly preferred), the forms in this repository that already
   satisfy the rule and must never be flagged. The near-miss is where a learned rule does damage.
6. **`## Example`**, the real code or comment the rule came from, quoted.
7. **`## Exceptions`**, what NOT to flag. Non-empty. A rule with no stated boundary cannot be
   activated.
8. **`<details><summary>Provenance</summary>`**, evidence, reviewer or session spread, and the
   activation argument. Collapsed, because it is background rather than review-time material.

---

## How a candidate graduates to active

Promotion is a deliberate act, recorded in the file and in the tables below. A candidate becomes
`active` only when **all** of these hold:

1. **Independent recurrence.** The rule is grounded in at least **3 independent occurrences**: three
   PRs, three review comments from more than one reviewer, or three distinct defects on different
   surfaces. For a rule seeded from shipped defects rather than mining, the three occurrences must
   not all be the same incident described in three places.
2. **New signal.** It catches something no lens in `agents/` already catches. Check the closest ones
   by hand and record the result in the provenance block. If a hand curated lens covers it, the right
   move is to say so and retire the candidate, not to activate a duplicate.
3. **A stated boundary.** A non-empty `## Exceptions` section, plus a `## Safe shapes` section when
   the repository contains a form that looks like the violation and is correct. Both seed rules below
   needed one.
4. **A replay.** Run the rule by hand against at least three merged changes that touch its `paths`
   and were **not** the evidence for it. Record what it would have said. A rule that would have fired
   on a correct change does not activate; it goes back for a sharper detection signal.
5. **Budget.** It fits the active budget, or it arrives with a named retirement candidate.

Write the outcome into the frontmatter (`status: active`, `activated: <date>`), move the row from the
candidate table to the active table with a one-line note saying what the replay showed, and leave the
`added` date alone.

**Demotion is normal.** An active rule that produces a false positive in a real review goes back to
`candidate` the same day, with the false positive recorded in its provenance block. That is cheaper
than an author learning to skim the learned-patterns section.

---

## Active rule budget

Cap active rules at **8**.

Scribe's review surface is narrow and already covered by more than twenty hand curated lenses, so the
empirical layer earns its place by being small. When an activation would exceed the budget, name a
retirement candidate first: weakest evidence, lowest hit rate since activation, or largest overlap
with a stronger rule. Do not activate past the cap.

## Retirement

Retire a rule when a hand curated lens absorbs the behavior, when the code shape it guards no longer
exists, when it produced repeat false positives, or when it loses to a stronger overlapping rule. Set
`status: retired` and `retired: <date>`, add `superseded-by` when something replaced it, move its row
to the retired table, and leave the file on disk. The file is the provenance; deleting it throws away
the reason anyone believed the rule in the first place.

---

## Current state

### Candidates

| Rule | Surface | Severity cap | Evidence | Blocking on |
| --- | --- | --- | --- | --- |
| `2026-08-23-wait-for-activation-before-input.md` | `text-injection` | `important` | 2 defects, both recorded in code comments: the write-back path in `TextActionController` and the clipboard read path in `SelectionReader` | A third independent occurrence, and a replay against merged changes under `src/Scribe.Core/TextInjection/**`. The two known occurrences are the same mechanism on the read and write sides, which is one pattern seen twice rather than three times. |
| `2026-08-23-guard-must-prove-the-operation.md` | `text-injection` | `important` | 3 defects: the machine-wide sequence-number proof, the payload-comparison proof that replaced it, and the sequence-number guard in `RestoreClipboard` that fired on every capture. `HookLivenessProbe` is a fourth instance of the same shape outside the clipboard. | A replay. The detection signal has to separate the invalid positive direction from the valid negative direction that `TextInjector.PasteViaClipboard` uses correctly today, and that separation has not been tested against real diffs yet. |

### Active rules

None. See the note under [Status model](#status-model): this is the expected state today, and
`agents/learned-patterns.md` reports its no-active-rules line until it changes.

### Retired rules

None.
