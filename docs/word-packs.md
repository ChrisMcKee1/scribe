# Word packs

Teach Scribe the words your work uses.

Scribe's speech model writes what it hears, so a product name, an acronym or a term only your team uses can
come out wrong: "cube control" for kubectl, "a k s" for AKS, "sky lens" for SkyLens. A word pack is a list of
fixes for words like these. Each entry says what Scribe hears and what it should write instead, and Scribe
applies them on your PC as you dictate.

Scribe comes with eleven built-in word packs, from AI model names to Azure, GitHub and .NET. This guide shows how
to make your own, by hand or with an AI assistant, for your organization's acronyms, product and project names,
your industry's terms and the names you say every day.

This page is written for AI assistants too. Give an assistant its link and ask for a word pack:
[For AI assistants](#for-ai-assistants) has everything it needs. Assistants that use skills can also use the
[scribe-word-pack skill](../.claude/skills/scribe-word-pack/SKILL.md), which checks the file it makes.

- [Why make a word pack](#why-make-a-word-pack)
- [Word packs and Your words](#word-packs-and-your-words)
- [Make a word pack in Scribe](#make-a-word-pack-in-scribe)
- [Write a word pack file](#write-a-word-pack-file)
- [Choose what Scribe hears](#choose-what-scribe-hears)
- [How Scribe applies a word pack](#how-scribe-applies-a-word-pack)
- [Examples](#examples)
- [Import and turn on a word pack](#import-and-turn-on-a-word-pack)
- [Share and update a word pack](#share-and-update-a-word-pack)
- [AI cleanup and your word packs](#ai-cleanup-and-your-word-packs)
- [Check a word pack before you share it](#check-a-word-pack-before-you-share-it)
- [For AI assistants](#for-ai-assistants)

## Why make a word pack

- **Right the first time.** Names, acronyms and jargon come out spelled your way, so you fix less after you
  dictate.
- **One file to share.** A word pack is a CSV file. Export it, send it to your team, and everyone's Scribe
  writes the same terms the same way.
- **On or off as a set.** Turn on a word pack for a project, a client or a class, and turn it off when you're
  done, without touching your own words.
- **Stays on your PC unless you choose.** A word pack works on your PC. A word pack you make or import is kept
  out of AI cleanup until you turn on **Use in AI cleanup** for it.
- **Helps AI cleanup too.** With **Use in AI cleanup** on, AI cleanup gets the word pack's words as vocabulary,
  so it can spell them your way even when speech recognition gets close but not exact.

Good candidates for a word pack:

- **Your organization's acronyms and internal tools,** the ones in your onboarding glossary: OKR, QBR, the
  name of your deployment tool, your teams' names.
- **Product, project and code names,** especially coined or unusually spelled ones: DataForge, SkyLens,
  Project Falcon.
- **Your industry's terms:** clinical abbreviations, legal and financial terms, engineering and scientific
  vocabulary, part numbers you say aloud.
- **Names:** colleagues, customers, partners and places that speech recognition misspells, such as Siobhan or
  Fabrikam.

## Word packs and Your words

Settings, **Dictionary** has two tabs:

- **Your words** is your own list. If a word pack writes a word differently, yours wins.
- **Word packs** holds the built-in word packs and the ones you make or import. You turn each one on or off
  as a set, and your own word packs win over built-in ones when both have the same words.

Use Your words for a handful of personal fixes. Use a word pack for a set of related terms you want to turn on
together or share with other people. Both use the same file format, so a file you write for one also imports
into the other (Your words, **More**, **Import from a CSV file...**).

## Make a word pack in Scribe

For a few words, make the word pack right in Scribe:

1. Open Settings from the Scribe icon in the notification area, then go to **Dictionary**, **Word packs**.
2. Choose **New word pack**. To give it a name, choose **More**, **Rename**.
3. Choose **Add word**, type what Scribe hears and what Scribe writes, and repeat for each word.
4. Choose **Save**.

A word pack you make this way starts turned on. For more than a few words, a file is quicker: write it in a
text editor or a spreadsheet, or ask an AI assistant to write it, then import it.

## Write a word pack file

### A complete example

```csv
# name: Contoso acronyms
# category: Contoso
# description: Acronyms and internal tool names used across Contoso.
pattern,replacement
#
# --- Planning ---
o k r,OKR
okr,OKR
okrs,OKRs
q b r,QBR
qbr,QBR
qbrs,QBRs
#
# --- Contracts ---
# "sow" on its own is an everyday word, so only the spelled-out form.
s o w,SOW
r f p,RFP
rfp,RFP
#
# --- Internal tools ---
contoso connect,Contoso Connect
data forge,DataForge
dataforge,DataForge
sky lens,SkyLens
skylens,SkyLens
contoso limited,"Contoso, Ltd."
```

Save it as a `.csv` file, for example `contoso-acronyms.csv`, and [import it](#import-and-turn-on-a-word-pack).

### The format, line by line

| Part | What it does |
| --- | --- |
| `# name:`, `# category:`, `# description:` | Name the word pack and say what it's for. All three are optional: without a name, Scribe names the word pack after the file. Don't use double quotes (") in them. |
| `pattern,replacement` | The column header. Optional, but it makes the file easier to read. The full header is `pattern,replacement,whole_word,enabled`. |
| `o k r,OKR` | One entry per line: what Scribe hears (**pattern**), a comma, then what Scribe writes (**replacement**). |
| Lines starting with `#` | Comments. Use them to name sections. |
| Blank lines | Ignored. |

Each row can have two more columns, and most rows don't need them:

| Column | In Scribe | Values | If you leave it out |
| --- | --- | --- | --- |
| `pattern` | **Scribe hears** | The words as speech recognition writes them. Required. | |
| `replacement` | **Scribe writes** | What Scribe types instead. | |
| `whole_word` | **Whole words only** | `true` or `false` (`yes`, `no`, `1` and `0` work too) | `true` |
| `enabled` | **Use** | `true` or `false`: off keeps the word in the word pack without using it | `true` |

A few details:

- **Commas and double quotes.** Put a value that holds a comma or a double quote in double quotes, and write
  each double quote inside it twice: `contoso limited,"Contoso, Ltd."` and `quote tool,"The ""Q"" tool"`.
- **Spaces.** Spaces at the start and end of a value are ignored, unless the value is in double quotes. Keep
  one space between words.
- **Encoding.** Save the file as UTF-8, so accented letters come through the same on every PC. In Excel, choose
  **CSV UTF-8 (Comma delimited)**. Scribe reads other files with your PC's Windows code page.
- **Size.** A file can have up to 50,000 rows and 10 MB, and a value up to 2,000 characters. Bigger isn't
  better, though: every entry can change what you dictate, and AI cleanup's vocabulary has room for only so
  many words (see [AI cleanup and your word packs](#ai-cleanup-and-your-word-packs)).
- **Files Scribe exports** start with a `# formula-guard: 1` line, which protects values that start with `=`,
  `+`, `-` or `@` when a spreadsheet opens the file. Keep that line when you edit an exported file, and leave it
  out of a file you write yourself.

### Editing in a spreadsheet

A spreadsheet is handy for a long list, but it can change what you typed: it may turn a value into a date or a
number, and it drops the quotes around a value that starts with `#`, which turns that row into a comment. A text
editor is the safest place for the final edit. Scribe ignores the empty cells a spreadsheet adds after the `#`
lines.

## Choose what Scribe hears

This is the part that decides whether a word pack works. Scribe hears (`pattern`) has to match what speech
recognition writes, not how the word is spelled.

1. **Start from what Scribe hears.** Open Settings, **Try dictation**, and dictate a sentence with the term.
   Scribe shows what it heard before any change. Use that, in lowercase, as **Scribe hears**. Say it a few
   times, and add each version that comes out differently.
2. **Cover the ways people say it.**
   - Letters said one at a time: add the letters with spaces and joined, `o k r` and `okr` for OKR.
   - Sound-alikes: `get hub` and `git hub` for GitHub.
   - Words split or joined: `dev ops` and `devops` for DevOps.
   - Numbers, which often come out as words: `q three` for Q3.
   - Plurals and other endings, which are separate words to Scribe: `okrs` for OKRs, because `okr` doesn't
     change "okrs". Possessives are covered: `okr` also changes "okr's" to "OKR's".
3. **Skip what Scribe already gets right.** If Try dictation shows a term spelled right, it doesn't need an
   entry, unless you want AI cleanup to see it as vocabulary.
4. **Avoid everyday words.** An entry changes its words everywhere you say them, so `it` written as `IT` would
   turn every "it" you dictate into "IT". When an acronym or name is also an everyday word (IT, US, SOW, Will,
   Grace), or a word has an everyday meaning besides yours (id, cert, docket, epic), use a longer phrase that only
   means your term, or leave it to AI cleanup, which can tell them apart from the sentence. A word's details mark
   short everyday words as common, including ones from the other languages Scribe recognizes, such as "la", "de"
   and "die", but check longer words yourself.
5. **Fix spellings, not words.** An entry that shortens a phrase to its abbreviation (`request for production`
   written as `RFP`), spells out an abbreviation, or swaps a word for another (`versus` written as `v.`) changes
   what you said every time you say it. Add one only if that's what you want.
6. **Keep Whole words only on.** With it off, an entry also changes text inside longer words: `net` written as
   `.NET` turns "network" into ".NETwork".
7. **Don't only change capital letters to lowercase.** Capital letters in Scribe hears don't matter, so an
   entry that writes `distillation` for `distillation` also turns "Distillation" at the start of a sentence
   into "distillation". Only do that for a name that is always lowercase, like npm or kubectl.
8. **One entry for each Scribe hears.** Several ways of hearing one term are fine: they're separate entries
   with the same Scribe writes. The same Scribe hears twice with different spellings is a mistake, and the
   import keeps only the first.
9. **Keep Scribe writes to one line.** AI cleanup gets a word as vocabulary only when what Scribe writes is one
   line of up to 100 characters. For longer text, such as a signature or an address, use a voice snippet
   instead.

## How Scribe applies a word pack

- **Capital letters don't matter** in Scribe hears: `okr` changes "okr", "Okr" and "OKR". Scribe writes is
  typed exactly as you wrote it.
- **Everything else has to match,** including hyphens and dots: `a k s` changes "A K S" but not "a.k.s." or
  "a-k-s". Add those as their own entries if Scribe hears them that way.
- **Whole words only** means an entry changes only a whole word or phrase, never part of a longer word.
- **When entries overlap,** the one that starts first wins, and at the same start the longer one. So you can
  have both `azure devops` and `devops` without one breaking the other.
- **A word already written right is left alone.** With `york` written as `New York`, "New York" stays "New
  York" rather than becoming "New New York".
- **Your words win over word packs,** and your own word packs win over built-in ones.
- **With AI cleanup on,** AI cleanup works on what Scribe heard, and then Scribe applies your words and word
  packs to its answer.
- **Word packs apply only while Apply your dictionary and snippets is on** (Settings, **Advanced**, **Text
  changes**). It's on unless you turned it off.

## Examples

Every example below imports as it is. The ways Scribe hears each term are typical ones: check yours in
[Try dictation](#choose-what-scribe-hears), because they depend on how you and your colleagues say them.

### Internal acronyms

The [complete example](#a-complete-example) above is one: an organization's planning terms, contract terms and
internal tools, with spelled-out and joined forms for each acronym.

### Industry terms

A clinic's cardiology abbreviations:

```csv
# name: Cardiology
# category: Healthcare
# description: Cardiology abbreviations for clinic notes.
pattern,replacement
a fib,AFib
afib,AFib
e c g,ECG
ecg,ECG
e k g,EKG
ekg,EKG
c h f,CHF
chf,CHF
stemi,STEMI
n stemi,NSTEMI
nstemi,NSTEMI
t a v r,TAVR
tavr,TAVR
```

The same approach works for legal terms, finance metrics (EBITDA, CapEx, YoY), engineering standards, lab
methods and part numbers.

### Product and project names

```csv
# name: Fabrikam products
# category: Fabrikam
# description: Fabrikam product and project names.
pattern,replacement
fabrikam one,Fabrikam One
fabricam one,Fabrikam One
fabricam,Fabrikam
project falcon,Project Falcon
sky lens,SkyLens
skylens,SkyLens
fab cloud,FabCloud
fabcloud,FabCloud
```

### People and customer names

```csv
# name: People we work with
# category: Names
# description: Colleagues and customers whose names speech recognition misspells.
pattern,replacement
shivon,Siobhan
shavon,Siobhan
neeve,Niamh
john paul,Jean-Paul
northwind traders,Northwind Traders
```

Leave out names that are also everyday words, such as Will, Grace or Bill: an entry for "will" would change
every "will" you dictate.

## Import and turn on a word pack

1. Open Settings, then go to **Dictionary**, **Word packs**.
2. Choose **Import a word pack...**, and pick the file.
3. Check the preview. It shows the word pack's name, which you can change, how many words it adds, and any rows
   Scribe couldn't read, with their row numbers and why. If the file has the same words twice with different
   spellings, choose **Keep mine** to keep the first or **Use the file's version** to take the last. Then
   choose **Import *N* valid words**.
4. The new word pack starts turned off. Select it and turn on **Use this word pack**. Turn on **Use in AI
   cleanup** too if you want AI cleanup to get its words as vocabulary (see
   [AI cleanup and your word packs](#ai-cleanup-and-your-word-packs)).
5. Choose **Save**.
6. Dictate a sentence with a few of its words in **Try dictation**, to see what Scribe heard and what it typed.

If a word doesn't change:

- Check that the word pack is on and that you saved.
- Compare what Scribe heard in Try dictation with Scribe hears, including spaces, hyphens and plural endings.
- Check that Your words doesn't have the same words written differently, because yours win.
- Check that **Apply your dictionary and snippets** is on (Settings, **Advanced**, **Text changes**).

## Share and update a word pack

- **Share:** select the word pack, choose **Export...**, and save the CSV file wherever your team shares files.
  The file includes words you turned off. A built-in word pack exports as a copy of its current words.
- **Update:** importing always makes a new word pack. To replace an older version, import the new file, turn it
  on, then select the old one and choose **More**, **Delete word pack...**. A deleted word pack stays in
  **Recently deleted** (the **More** button above the list) for 30 days.
- **Start from a built-in:** choose **More**, **Duplicate** on any word pack, then edit the copy.

## AI cleanup and your word packs

A word pack always works on your PC, with AI cleanup or without it. **Use in AI cleanup** decides whether AI
cleanup also gets the word pack's words as vocabulary: what Scribe writes, and what Scribe hears where that's
different. That helps the model spell your terms your way, including when speech recognition gets close but not
exact.

- **Where the words go.** When AI cleanup runs on your PC, they stay on your PC. With Microsoft Foundry, GitHub
  Copilot or another AI service, the words a dictation appears to mention go to that service with its cleanup
  request, and the rest stay on your PC.
  [Privacy](../PRIVACY.md#optional-ai-features-and-data-transmission) lists exactly what each request carries.
- **Off until you choose.** Word packs you make or import start with Use in AI cleanup off. Built-in ones start
  with it on.
- **Confidential terms.** Keep unreleased product names, customer names and other confidential terms out of AI
  cleanup unless AI cleanup runs on your PC or with a service your organization approves. A dictation that
  mentions such a term would send it. The word pack still fixes them on your PC.
- **Room.** Each cleanup request holds up to 5,000 words and 24,000 characters of vocabulary, or 80 words with
  the short instructions, your words first, picked from the ones the dictation appears to mention. A word that
  doesn't fit is still fixed on your PC. Since Scribe 0.5.2 a large word pack no longer crowds out the words a
  dictation needs, because each request carries the words that dictation mentions rather than the start of the
  whole list.

## Check a word pack before you share it

- [ ] The file is saved as UTF-8, and its first row after the `#` lines is `pattern,replacement`.
- [ ] It has a `# name:` line, with no double quotes in the name, category or description.
- [ ] Every **Scribe hears** is lowercase, with single spaces between words and none at the ends.
- [ ] No **Scribe hears** appears twice.
- [ ] No **Scribe hears** is an everyday word on its own, such as "it", "us" or "will", or a word with another
  everyday meaning, such as "id" or "docket".
- [ ] Every entry fixes a spelling. None shortens a phrase to an abbreviation or spells one out, unless you
  want that.
- [ ] Whole words only is on for every entry (or the column is left out).
- [ ] Values with commas or double quotes are in double quotes.
- [ ] You tried a few of its words in Try dictation.

To check a file automatically, download
[check_word_pack.py](../.claude/skills/scribe-word-pack/scripts/check_word_pack.py) from the scribe-word-pack
skill and run it on the file. It needs Python 3.8 or later:

```powershell
python check_word_pack.py contoso-acronyms.csv
```

It reads the file the way Scribe's import does and lists any row Scribe would skip, any **Scribe hears** that
appears twice, rows that shorten a phrase to an abbreviation or spell one out, and each word Scribe would mark
in its details. It can't know every everyday word, so read your one-word entries yourself too.

## For AI assistants

This section tells an AI assistant how to make a Scribe word pack. The rest of this page explains why each
rule matters.

### How to ask an assistant

Copy this, fill in the parts in angle brackets, and give it to an AI assistant:

```text
Make me a Scribe word pack for <the subject, such as our team's acronyms or cardiology terms>.
Follow the instructions at https://github.com/ChrisMcKee1/scribe/blob/main/docs/word-packs.md
Here is what to build it from: <paste a glossary, an acronym list, product names or a document>
```

If the assistant can't open GitHub pages, give it the raw file instead:
https://raw.githubusercontent.com/ChrisMcKee1/scribe/main/docs/word-packs.md

### Instructions for the assistant

You're making a CSV file that Scribe, an offline dictation app for Windows, imports as a word pack. Each row
maps what speech recognition writes when someone says a term (`pattern`) to how the term should be written
(`replacement`). Scribe swaps them as the person dictates.

1. **Understand the subject.** Work from what the person gave you: a glossary, an acronym list, product names,
   documents. If they gave you nothing and the subject is unclear, ask what the word pack is for before you
   start. Don't invent terms, and don't add a term you aren't sure is spelled right.
2. **Pick the terms speech recognition gets wrong:** acronyms and initialisms, coined names, names with unusual
   spelling or capitalization (DataForge, iPhone, SkyLens), terms borrowed from other languages, and specialist
   jargon. Leave out ordinary words that are spelled the usual way.
3. **Write each term's written form exactly** as the person's source writes it, with its capitals, hyphens and
   punctuation. That's the `replacement`.
4. **Write the ways speech recognition may write it.** Each is a row with the same `replacement`:
   - All lowercase, with single spaces between words and none at the ends.
   - Acronyms said letter by letter: the letters with spaces (`k p i`) and joined (`kpi`). Acronyms said as a
     word: the word (`nasa`).
   - Names split into ordinary words or joined together: `sky lens` and `skylens`.
   - Likely mishearings and sound-alikes: `get hub` for GitHub, `fabricam` for Fabrikam.
   - Numbers as words: `q three` for Q3, `gpt five` for GPT-5.
   - Plurals as separate rows when people use them: `kpis` for KPIs.
   - Nothing with symbols speech recognition doesn't write, such as `#`, `@`, `/` or `_`.
5. **Leave out rows that would cause harm:**
   - A `pattern` that is an everyday word on its own ("it", "us", "will", "sow", "la", "de"), or a word with an
     everyday meaning besides the term ("id", "cert", "docket", "epic"), because it would change that word in
     every sentence. Read every one-word pattern and ask whether it could turn up in an ordinary sentence. If it
     could, use a longer phrase that only means the term, or leave the term out.
   - A row that changes the words rather than their spelling: a phrase shortened to its abbreviation
     ("request for production" as RFP), an abbreviation spelled out, or a word swapped for another ("versus" as
     v.). Add one only if the person asks for it.
   - A row whose `replacement` only makes the `pattern` lowercase, unless the name is always lowercase (npm). A
     term speech recognition already writes right needs no row.
   - A second row with the same `pattern`, even with different capitals.
   - A `replacement` longer than 100 characters or spanning lines.

   Leave out the `whole_word` and `enabled` columns: both default to true, which is what almost every row needs.
6. **Write the file:**
   - The first lines are `# name: <Word pack name>`, `# category: <a short category>` and `# description: <one
     sentence>`, with no double quotes in them.
   - Then the header row `pattern,replacement`.
   - Then the rows, grouped under comment lines such as `# --- Planning ---`.
   - Put a value in double quotes if it holds a comma or a double quote, and write each double quote inside it
     twice.
   - A few dozen to a few hundred rows is typical. Don't pad the file with terms nobody said they need.
7. **Check it.** If you can run Python 3.8 or later, download
   https://raw.githubusercontent.com/ChrisMcKee1/scribe/main/.claude/skills/scribe-word-pack/scripts/check_word_pack.py,
   run `python check_word_pack.py <file>.csv`, and fix every error it lists. Otherwise, go through the
   [checklist](#check-a-word-pack-before-you-share-it) yourself.
8. **Hand it over.** Save it as a UTF-8 file named after the word pack, such as `contoso-acronyms.csv`, or, if
   you can't make files, put the whole file in one code block. Then tell the person, briefly:
   - How to import it: Settings, Dictionary, Word packs, Import a word pack..., then turn on Use this word
     pack and Save.
   - To try a few of the words in Try dictation and adjust any row that doesn't match what Scribe heard.
   - That Use in AI cleanup starts off, and whether to turn it on depends on where their AI cleanup runs and
     how confidential the terms are.
   - Which rows you were unsure of.
