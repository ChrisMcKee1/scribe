---
name: scribe-word-pack
description: Create, check or fix a word pack for Scribe, the offline push-to-talk dictation app for Windows. A word pack is a CSV file that maps what speech recognition writes when someone says a term (pattern) to how the term should be written (replacement), so acronyms, product and project names, industry jargon and people's names come out spelled right. Use this skill whenever someone asks for a Scribe word pack, a custom dictionary or vocabulary list for Scribe, or wants to turn an acronym list, internal glossary, onboarding document, product catalog, style guide or industry terminology into something Scribe can import, even if they never say "word pack". Also use it to review or repair an existing Scribe word pack CSV.
---

# Scribe word pack

Scribe's speech model writes what it hears, so kubectl comes out as "cube control" and AKS as "a k s". A word
pack is a CSV file of fixes: each row says what speech recognition writes (`pattern`, shown in Scribe as
**Scribe hears**) and what Scribe should type instead (`replacement`, shown as **Scribe writes**). Scribe swaps
them on the person's PC as they dictate. If they turn on **Use in AI cleanup** for the word pack, AI cleanup also
gets, as vocabulary, the words each dictation appears to mention.

The full guide, for people and assistants alike, is `docs/word-packs.md` in the Scribe repository
(https://github.com/ChrisMcKee1/scribe/blob/main/docs/word-packs.md). Read it when you need the Settings steps,
more examples or the reasoning behind a rule. Everything needed to build a correct file is below.

## Workflow

1. Understand the subject and gather the source material.
2. Choose the terms that need a fix.
3. Write the patterns for each term.
4. Drop the rows that would do harm.
5. Write the file.
6. Check it with `scripts/check_word_pack.py`.
7. Hand it over with short import steps.

## 1. Understand the subject

Work from what the person gives you: a glossary, an acronym list, product names, documents, a website. If they
name only a field ("cardiology", "our sales acronyms") and nothing else, ask once for source material or how
they'll use it, then work from well-established terms of that field. Never invent a term or guess a spelling:
a wrong spelling in a word pack gets typed into every dictation that mentions it. Leave out anything you're not
sure of and say so at the end.

Ask, or infer from the source, whether the terms are confidential (unreleased product names, customer names).
That decides what you tell them about AI cleanup when you hand the file over.

## 2. Choose the terms

Pick the terms speech recognition is likely to get wrong: acronyms and initialisms, coined or compound names
(DataForge, SkyLens), unusual spelling or capitalization (iPhone, GitHub), names of people and places, words
borrowed from other languages, and specialist jargon. Skip ordinary words spelled the usual way, which speech
recognition already writes correctly; they only add rows that can misfire.

## 3. Write the patterns

A pattern has to match what speech recognition writes, not how the term is spelled. Write each one as Scribe
would hear it, and add a row for every likely version, all with the same replacement:

- **Lowercase, single spaces, nothing at the ends.** Matching ignores case, so lowercase keeps the file readable.
  A space at either end, two spaces in a row, a tab or a no-break space makes the row miss.
- **Letters said one at a time:** the letters with spaces and joined, `k p i` and `kpi` for KPI.
- **Acronyms said as a word:** the word, `nasa` for NASA.
- **Compound names:** split and joined, `sky lens` and `skylens` for SkyLens, `dev ops` and `devops` for DevOps.
- **Likely mishearings and sound-alikes:** `get hub` and `git hub` for GitHub, `fabricam` for Fabrikam, `shivon`
  for Siobhan.
- **Numbers as words:** `q three` for Q3, `gpt five` for GPT-5.
- **Plurals and other endings as their own rows** when people use them: `kpis` for KPIs. A row for `kpi` does not
  change "kpis" (it matches whole words only), though it does change "kpi's".
- **Hyphens and dots match exactly:** `a k s` does not change "a-k-s" or "a.k.s.", so add those forms only if
  speech recognition writes them.
- **No symbols speech recognition doesn't write,** such as `#`, `@`, `/` or `_`.

The replacement is typed exactly as written, so copy the term's spelling from the source, capitals, hyphens and
punctuation included.

## 4. Drop the rows that would do harm

A row changes its words everywhere the person says them, in every app. Each of these hurts more than it helps:

- **An everyday word as the pattern:** `it` for IT turns every "it" into "IT". The same goes for us, will, sow,
  grace, bill, and short words in the other languages Scribe recognizes, such as la, de, die, el and il. It also
  goes for any word with a second, everyday meaning: `id` written as `id.` breaks "my user ID", `verses` written as
  `v.` breaks poetry, `epic` for the Epic system breaks "an epic launch". Read every one-word pattern and ask
  whether it could turn up in an ordinary sentence; if it could, use a longer phrase that only means the term,
  or leave the term out. AI cleanup can often tell them apart from context.
- **A row that changes the words instead of their spelling:** a phrase shortened to its abbreviation
  (`request for production` written as `RFP`), an abbreviation spelled out (`rfp` written as `Request for
  Proposal`), or a word swapped for its abbreviation (`versus` written as `v.`). People say the long form when
  they want it written. Add one only when the person asks for it.
- **A row that only lowercases:** `distillation` for `distillation` also lowercases "Distillation" at the start of
  a sentence. Keep one only for a name that is always lowercase, like npm or kubectl. A term speech recognition
  already writes correctly needs no row at all.
- **The same pattern twice** with different replacements, capitals included: the import keeps only the first.
  Several patterns for one replacement are fine.
- **A replacement longer than 100 characters or spanning lines:** Scribe still applies it, but AI cleanup never
  gets it as vocabulary. Long text such as a signature belongs in a Scribe voice snippet, not a word pack.
- **An empty replacement:** it deletes the spoken words and can leave stray punctuation.

Leave out the `whole_word` and `enabled` columns. Both default to true, and whole-word matching is what keeps
`net` from turning "network" into ".NETwork".

## 5. Write the file

Use exactly this shape, saved as UTF-8:

```csv
# name: <Word pack name>
# category: <Short category>
# description: <One sentence saying what the word pack covers.>
pattern,replacement
#
# --- <Section name> ---
<pattern>,<replacement>
<pattern>,<replacement>
```

- The three `#` lines name and describe the word pack. Never put a double quote in them: Scribe refuses one there.
- Group rows under `# --- Section ---` comment lines. Any line that starts with `#` is a comment.
- Put a value in double quotes when it holds a comma or a double quote, and double each quote inside it:
  `contoso limited,"Contoso, Ltd."`.
- Don't add `# formula-guard:`, `# scribe-format:` or `# based-on:` lines. Scribe writes those in its own exports,
  where they change how values are read.
- A few dozen to a few hundred rows is typical. Don't pad the file with terms nobody asked for; every row is one
  more chance to change a word the person didn't mean.

Name the file after the word pack in lowercase with hyphens, such as `contoso-acronyms.csv`.

## 6. Check it

Run the checker that ships with this skill. It needs Python 3.8 or later and reads the file with the same rules
as Scribe's import:

```bash
python <this skill's folder>/scripts/check_word_pack.py contoso-acronyms.csv
```

- **ERROR** lines are rows Scribe would skip or get wrong. Fix every one and run it again until it reports 0 errors
  (exit code 0).
- **WARNING** lines are the harms listed above. Fix them unless the person asked for exactly that.
- **NOTE** lines are for information.

The checker can't know every English word, so it won't catch a pattern like `id` or `docket` that has an
everyday meaning. Reading your one-word patterns yourself is part of the check.

If you can't run Python, check by hand: UTF-8, the header row, no double quotes in the `#` lines, every pattern
lowercase with single spaces, no pattern twice, no everyday word as a pattern, no row that shortens or spells out
a phrase, and quotes around values with commas.

## 7. Hand it over

Save the file, or if you can't create files, give the whole file in one code block. Then tell the person, in a
few lines:

- How to import it: open Scribe's Settings, then **Dictionary**, **Word packs**, **Import a word pack...**; pick
  the file and confirm the import in the preview. The word pack arrives turned off: select it, turn on **Use this
  word pack**, and choose **Save**.
- To say a few of the words in **Try dictation** (in Settings), which shows what Scribe heard, and to adjust any
  pattern that doesn't match it.
- That **Use in AI cleanup** starts off. Turning it on sends the words a dictation mentions to wherever their AI
  cleanup runs, so for confidential terms they should leave it off unless AI cleanup runs on their PC or with a
  service their organization approves.
- Which terms you left out or weren't sure of.

## Fixing an existing word pack

Run the checker on it first. Keep the person's rows and intent: fix what the checker reports, add missing
variants, and explain anything you remove and why. A file exported from Scribe starts with a
`# formula-guard: 1` line; keep it, because it tells Scribe how to read values that start with `=`, `+`, `-` or
`@`.

## Example

The person pastes: "Our acronyms: CSAT (customer satisfaction), QBR (quarterly business review), SOW
(statement of work). Our internal tool is called DataForge."

```csv
# name: Contoso acronyms
# category: Contoso
# description: Contoso's business acronyms and internal tool names.
pattern,replacement
#
# --- Business acronyms ---
c sat,CSAT
c-sat,CSAT
csat,CSAT
q b r,QBR
qbr,QBR
qbrs,QBRs
# "sow" on its own is an everyday word, so only the spelled-out form.
s o w,SOW
#
# --- Internal tools ---
data forge,DataForge
dataforge,DataForge
```

Then the hand-over: how to import it, to try "CSAT", "QBR" and "DataForge" in Try dictation, that `sow` was left
out on purpose, and that Use in AI cleanup starts off.
