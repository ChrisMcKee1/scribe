# scribe-word-pack

An agent skill that makes, checks and fixes Scribe word packs: CSV files that tell Scribe how to write the
acronyms, names and jargon its speech model gets wrong. The guide for people is
[docs/word-packs.md](../../../docs/word-packs.md); this skill is the same rules in a form an agent follows, plus a
checker.

## Layout

```
SKILL.md                      what the agent reads: the workflow, the rules and the file format
scripts/check_word_pack.py    reads a CSV the way Scribe's import does and lists what to fix
```

## Use it

Agents that read skills from `.claude/skills`, such as GitHub Copilot CLI and Claude Code, pick it up in this
repository without setup. Ask for a word pack ("make me a Scribe word pack from this acronym list") or to check
one. To use it anywhere else, copy this folder into your personal skills folder, such as `~/.copilot/skills/` or
`~/.claude/skills/`.

The checker needs Python 3.8 or later and nothing else:

```
python scripts/check_word_pack.py my-word-pack.csv
```

It exits with 0 when the file imports cleanly, 1 when something needs fixing, and 2 when a file can't be read.

## Keep the checker in step with Scribe

`check_word_pack.py` mirrors Scribe's word pack import (`LibraryCsvCodec.ReadImport` and the classes it uses in
`src/Scribe.Core/Libraries`) and the hints of `LibraryTermLint`. After changing any of them, update the script to
match and run its self-test, which compares every import case in the shared fixtures:

```
python .claude/skills/scribe-word-pack/scripts/check_word_pack.py --self-test tests/fixtures/libraries/csv
```
