# Scribe 0.2.20

AI cleanup now receives your whole vocabulary instead of the first 80 terms, the dictionary is
easier to edit, and it tells you when an entry is already handled by a library you have turned on.

## Fixed: your full vocabulary reaches AI cleanup

The glossary sent to AI cleanup was capped at 80 terms for every provider. That number made sense
for a small on-device model with a few thousand tokens of context, and no sense at all for a cloud
endpoint with room for a hundred times more.

The cap now follows where cleanup runs:

| Provider | Terms sent |
| --- | --- |
| Microsoft Foundry, OpenAI-compatible | effectively all of them |
| Foundry Local (on device) | 80 |

The real limit is now a size budget rather than an entry count, because an entry count has no
relationship to what a request actually costs: a dictionary of short acronyms was being cut off at
the same point as one full of long phrases.

This was worse than a low ceiling. Your own entries are merged ahead of library entries, so anyone
with 80 or more personal entries was sending **none** of their enabled libraries to the model, and
losing some of their own entries on top. Enabling a vocabulary library and seeing no difference in
AI cleanup was a symptom of this.

Local find-and-replace was never capped and is unchanged.

## New: a dictionary you can actually edit

The dictionary grid had two undiscoverable interactions: you added an entry by typing into a phantom
row at the bottom, and removed one by selecting it and pressing Delete. Neither is visible unless
somebody tells you.

- **Add entry** button. It appends a row, scrolls to it, and puts the cursor in the Spoken cell.
- A **remove button on every row**, so deleting no longer depends on knowing a keyboard shortcut.
- A **Library** column showing how each entry relates to the libraries you have switched on:
  *Same as library* means it is redundant, *Overrides library* means yours wins. Hovering explains
  what the library would have written instead.

The Library column updates as you type and as you toggle libraries on the Libraries page, so you can
see the effect of a change before saving. An override you no longer want can be turned off with its
Enabled checkbox rather than deleted, which leaves the library to take over.

## New: the dictionary tells you when a library already covers an entry

Save the dictionary and Scribe now checks it against the libraries you have switched on. If an entry
produces exactly what a library already produces, it offers to remove it, naming the entries and the
library they came from. Removing them changes nothing about your dictation and frees room in the
glossary for terms a model cannot guess, such as names, internal acronyms, and project codenames.

Entries that write the same spoken form **differently** are never offered for removal. Those are
deliberate overrides and yours wins. If a library maps "v s" to Visual Studio and you added "v s"
meaning "versus", deleting that would change what your dictation says. Scribe reports how many you
have and leaves them alone.

The prompt is a suggestion, not a gate. Declining it saves normally.

## Under the hood

- The glossary is bounded by total size rather than entry count, so a long dictionary of short terms
  is no longer penalized for the sake of one with long phrases.
- Truncation, where it still applies, drops from the end of the list. Your own entries are ordered
  first because they are the ones a model cannot infer, so they survive.
- New `DictionaryLibraryOverlapAnalyzer` in the core library classifies overlap as redundant or
  override, with tests covering casing-only differences, word-boundary differences, disabled entries
  on either side, and the shipped library data itself.
