# Scribe.StyleEval

**Side testing only. This project is never published and never ships.**

`build/pack.ps1` publishes exactly two projects, `src/Scribe.App` and `src/Scribe.Overlay`, and
packs the result with Velopack. Nothing under `tools/` is in that path. `Scribe.StyleEval.csproj`
sets `IsPackable=false` and `IsPublishable=false` as well, so the corpus, the checkers and the
Markdig dependency cannot reach a user's machine.

It grades the instruction sets that **do** ship:

| File | What it contributes |
|---|---|
| `src/Scribe.Core/TextActions/TextActionCatalog.cs` | The ten model-backed actions and their per-destination instructions |
| `src/Scribe.Core/TextActions/EnrichmentRules.cs` | Detection, Restraint and Preservation |
| `src/Scribe.Core/TextActions/TextActionPrompt.cs` | Composition order and the delimited user message |
| `src/Scribe.Core/TextActions/TextActionSanitizer.cs` | Output contracts and length bands |
| `src/Scribe.Core/Cleanup/CleanupPrompt.cs` | `DefaultWritingStyle`, the house style applied to every action |

The suite references `Scribe.Core` directly and calls `TextActionPrompt.BuildSystemPrompt`,
`TextActionPrompt.BuildUserMessage` and `TextActionSanitizer.Sanitize` rather than copying them, so
it can never drift into grading a stale snapshot of the prompts.

## Two halves

**Negative, deterministic.** Did the answer break a stated rule? Over-bolded, invented a
blacklisted heading, dropped a URL, emitted unparseable JSON, exceeded a length band, added a dash.
Mechanically checkable, cheap, perfectly reliable, and therefore code.

**Positive, missed opportunity.** Did the answer miss structure the content actually warranted? The
Detection rules say a deadline gets bolded, three peer items become a list, repeated records become
a table, identifiers get code formatting. A model that silently does none of that passes every
negative check while producing a worse result than a careful human editor. The mechanically
decidable part of that half lives here; the subjective part (does a Teams message read like a
colleague wrote it, is the agent brief actionable) belongs to the LLM judge, which consumes the
same results file.

A run reports the two sheets separately on purpose. One blended pass rate hides exactly the failure
this suite exists to find.

## Checkers

Negative:

| Check | Applies to | Fails when |
|---|---|---|
| `preservation` | all | a `protectedTokens` entry is not in the answer, in any legitimate encoding for that destination |
| `house-style` | all | a `spelledOutNumbers` phrase survived, the answer added an em or en dash, or an author's own dash was removed by a same-length action |
| `restraint-bold` | all but JSON | `expectNoBold` and bold appeared; or more than one bold per block, over four words, a whole line or sentence, or two bold runs back to back |
| `restraint-list` | all but JSON | `expectNoList` and the answer added a list the selection did not have, or a list holds fewer than three items |
| `heading-blacklist` | all but JSON | a heading matches Summary, Overview, Background, Context, Details, Analysis, Conclusion, Next steps, Key points, and the author did not write it themselves |
| `markdown-contract` | `format-markdown` | does not parse as CommonMark, an untagged fence, HTML, a missing blank line around a block, the whole answer wrapped in a fence, or a `**` left stranded in the prose |
| `html-contract` | `format-html` | not well formed as an escaped fragment, an element outside the allowlist, any attribute outside `href`/`colspan`/`rowspan`, an `on*` attribute inside a tag, an href outside http/https/mailto, a script/style/iframe/object/embed/form/input/svg/template, a doctype, html/head/body, a comment, or an unescaped author angle bracket or ampersand |
| `json-contract` | `format-json` | does not parse, a key that is not lowerCamelCase, an unjustified wrapper key, a string value that IS an absolute date and is not ISO 8601, or a string-typed value demoted to a JSON number |
| `teams-contract` | `format-for-teams` | an HTML tag, single-asterisk emphasis, a heading, a block quote, or a table, anywhere outside a code block |
| `length-band` | all | outside the action's own `TextActionLength` band, read back from the shipping sanitizer |
| `minimal-diff` | `fix-grammar` | normalized edit distance over 15%, or the sentence or paragraph count moved |

Positive:

| Check | Fails when |
|---|---|
| `should-bold` | a `shouldBold` phrase did not come back emphasised |
| `should-list` | `shouldList` and no list of three or more items (an array of three in JSON) |
| `should-table` | `shouldTable` and no table (Markdown, HTML) or uniform array of objects (JSON) |
| `should-code` | a `shouldCode` token did not come back in code formatting |

`should-bold` passes on partial coverage on purpose: Restraint caps emphasis at one phrase per
paragraph, so a scenario naming three eligible phrases cannot be satisfied by bolding all three.

### NotApplicable

`NotApplicable` is never a free pass. It means the checker had nothing to say, and it is reported
separately from a pass. A checker abstains when:

- the scenario carries no expectation of that kind (`protectedTokens` empty, `shouldBold` empty,
  `shouldList` false);
- the destination cannot express the construct: no emphasis or code in JSON, no table or heading in
  Teams, no heading in JSON;
- the action was not given `EnrichmentRules.Detection` at all, read off `TextAction.Enrichment`. A
  tone rewrite that adds no table is following its instruction, not failing it. This gates the whole
  positive half;
- the answer could not be parsed, so every checker that needed the parse tree says so identically
  rather than each inventing a verdict;
- the check belongs to another destination's contract (`markdown-contract` on a Teams cell), or to
  another checker (`restraint-list` on JSON, where `json-contract` grades arrays);
- `length-band` on an empty selection, or on a structural conversion of fewer than 80 characters,
  which is the exemption the shipping sanitizer already makes;
- `minimal-diff` on anything but `fix-grammar`, the one action that promises a minimal diff.

Four abstentions exist because the rulebook contradicts itself unless they do, and each is the
difference between a real number and a manufactured failure:

- `restraint-bold` does not apply `expectNoBold` to a **Teams** answer for a `shouldTable`
  scenario. The Teams instruction renders table content as one line per row with a bold label at the
  front, so that bold is instructed rather than chosen.
- `should-table` abstains on **Markdown and HTML** when `recordCount` is 2. Two records are a real
  repeated structure and JSON renders them as an array of two objects, but Restraint puts the table
  floor at three rows, so paired lines are the correct document answer.
- `should-table` abstains for `rewrite-for-ai`, which writes a brief rather than a document.
- `restraint-list` passes the lists `rewrite-for-ai` was told by name to produce (Constraints,
  Acceptance criteria), whatever their item count.

Two ceilings are counted against the selection rather than in absolute terms, because they govern
structure the model **introduced**: `restraint-list` subtracts the lists the selection already had
(a dictated diff hunk parses as a list), and `restraint-bold` subtracts the emphasis the author
typed themselves. `heading-blacklist` works the same way: a Summary heading the author wrote is
content, and deleting it would be dropping their text.

`shouldHeading` is carried in the schema and is **not** graded deterministically. Whether two
sections of two paragraphs each deserve a heading, and what that heading should be called once the
blacklist rules out every generic name, is a judgement call. It belongs to the judge.

## Corpus

`corpus/*.jsonl`, one JSON object per line, no wrapping array. The file name is the category and
every id must start with it. `CorpusLoader` fails loudly, naming file and line, on a malformed
entry, a duplicate id, a protected token that is not in the scenario's own text, dash metadata that
disagrees with the text, contradictory expectations, or a scenario carrying no expectation at all.

```json
{"id":"detection-001","category":"detection","text":"...","traits":["has-deadline"],
 "protectedTokens":["https://example.com/x"],"containsDash":false,"spelledOutNumbers":["twenty three"],
 "expectNoBold":false,"expectNoList":false,"shouldBold":["by Friday"],"shouldList":true,
 "shouldTable":false,"shouldHeading":false,"shouldCode":["src/main.ts"],"note":"what this tests"}
```

`recordCount` is optional and only meaningful beside `shouldTable`. Set it to 2 when exactly two
records share the fields: JSON still owes an array of two objects, while Markdown and HTML are below
Restraint's three-row table floor and owe paired lines instead. Leave it out otherwise; unset reads
as three or more.

One rule is an advisory rather than a rejection: a `shouldCode` token that does not appear
literally in the scenario's own text. That is correct for dictated input, where the selection says
"branch release slash one point four" and the house style writes `release/1.4`, so it is reported
after load instead of throwing.

Current corpus: 1,000 scenarios across `brain-dump`, `casual-chat`, `dictated-and-edge`,
`html-and-web`, `long-form-prose`, `markdown-and-headings`, `meeting-notes`, `professional-email`,
`structured-data` and `technical-notes`. Ten model-backed actions makes the full matrix exactly
10,000 cells.

## Running

```powershell
# What the current Azure sign-in can actually reach.
dotnet run --project tools/Scribe.StyleEval -- --list-deployments

# Calibration: two scenarios per category, all ten actions, real model, real sanitizer.
dotnet run --project tools/Scribe.StyleEval -- --sample 2

# Plan and cost only, no model calls.
dotnet run --project tools/Scribe.StyleEval -- --dry-run

# Full run, all 10,000 cells.
dotnet run --project tools/Scribe.StyleEval -- --concurrency 8

# Re-score the stored answers with today's checkers, no model calls.
dotnet run --project tools/Scribe.StyleEval -- --score-only
```

Results default to `tools/Scribe.StyleEval/results/style-eval.jsonl`, deliberately in the project
folder rather than in `bin`, so an ordinary `dotnet build` cannot throw away hours of paid work.
That folder is gitignored.

Results append to JSONL as each cell completes, so a crash costs one cell rather than the run.
Restarting skips any scenario-plus-action cell already in the file; `--no-resume` forces a re-run.

Every row keeps the raw response as well as the sanitized text, which is what makes `--score-only`
possible: the checkers and the shipping sanitizer are re-run locally over stored answers, so a
false positive can be fixed and re-measured without paying for 10,000 model calls again.

Throttling is retried, not recorded as an error. A calibration run at concurrency 10 against the
South Central `DataZoneStandard` deployment lost 22% of its cells to HTTP 429; with exponential
backoff and jitter the same run completes with zero transport errors. Concurrency 6 to 8 is the
comfortable range for that deployment's quota.

## Model

The generating model is the user's Azure `gpt-5.6-terra` deployment. The default endpoint is
`https://mtech-sc-resource.cognitiveservices.azure.com/` rather than the East US 2 resource that
also hosts it: `docs/gpt56-phonetic-benchmark.md` measured the South Central `DataZoneStandard`
deployments at roughly nine to eleven times lower latency than the `GlobalStandard` ones, which
over ten thousand cells is the difference between an afternoon and a weekend. Override with
`--endpoint` and `--deployment`.

Authentication is a subscription-scoped `AzureCliCredential`. That is not cosmetic: on a machine
signed in to several tenants, `az account get-access-token --tenant <id> --scope
https://ai.azure.com/.default` returns "interaction required" while `--subscription <id>` succeeds,
because a subscription selects the cached account as well as its tenant.
`AzureCredentialFactory` in `Scribe.Core` makes the same choice for the same reason. Override with
`--subscription` or `--tenant`.

## Relationship to tools/Scribe.Evals

Provider wiring is reused, not reimplemented. `Scribe.Evals` owns the Azure Responses client and is
the assembly `Scribe.Core` grants internals access to, so this project references it and calls
`DirectResponsesCleanupClient.SendAsync`. Two small additive changes were made there: an
`InternalsVisibleTo`, and a `SendAsync` overload that takes an already-composed user message so both
harnesses share one transport. Neither project is published.
