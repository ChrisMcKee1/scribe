namespace Scribe.Core.TextActions;

/// <summary>
/// The built-in text actions, in palette order.
/// </summary>
/// <remarks>
/// Two ordering decisions are deliberate. "Rewrite for an AI agent" and "Format for Teams" lead
/// because they are the two destinations most selections are actually headed for in this product: a
/// chat message, or a prompt to a coding agent. And the machine-readable conversions (Markdown,
/// HTML, JSON) are marked <see cref="TextAction.Advanced"/> so the default palette stays short
/// enough to scan without losing the capability for people who want it.
/// </remarks>
public static class TextActionCatalog
{
    /// <summary>Id of the deterministic vocabulary pass, the only action that runs with no model.</summary>
    public const string ApplyVocabularyId = "apply-vocabulary";

    /// <summary>Id of the open-ended spoken instruction path (issue 25's original ask).</summary>
    public const string VoiceInstructionId = "voice-instruction";

    /// <summary>Every built-in action, in the order the palette renders them.</summary>
    public static IReadOnlyList<TextAction> All { get; } =
    [
        new TextAction(
            "rewrite-for-ai",
            "Rewrite for an AI agent",
            "Turns a brain dump into a clear, ordered task list an agent can act on.",
            TextActionGroup.Rewrite,
            TextActionKind.Ai,
            Instruction:
                "Rewrite the text as a delegation brief for a competent AI coding agent that cannot " +
                "ask follow-up questions and cannot see the author's screen. " +
                "Lead with a single sentence naming the outcome the author wants. " +
                "When the text describes more than one piece of work, give the work as an ordered " +
                "list of steps, each step a concrete action with a verifiable result. " +
                "Pull any constraint, preference or exclusion the author stated into a short " +
                "Constraints list, and put anything they said should NOT happen into it explicitly, " +
                "because an unstated non-goal is the most common way a delegated task goes wrong. " +
                "When the author says how they will know the work is done, render that as an " +
                "Acceptance criteria list. " +
                "Resolve vague references: replace \"it\", \"that\" and \"the thing\" with the noun " +
                "the author meant, wherever the text makes the referent unambiguous. " +
                "Reproduce every file path, identifier, command, URL, version number, error string " +
                "and code fragment exactly as written, character for character. " +
                "Do not invent requirements, do not add steps the author did not ask for, and do not " +
                "add a preamble, a sign-off or an offer to help. " +
                "If the text describes only one small piece of work, return a single clear paragraph " +
                "rather than padding it into a structure it does not need.",
            UsesGlossary: true,
            Length: TextActionLength.Restructure,
            Enrichment: EnrichmentLevel.Full),

        new TextAction(
            "format-for-teams",
            "Format for Teams",
            "Chat-ready wording using the Markdown the Teams compose box understands.",
            TextActionGroup.Format,
            TextActionKind.Ai,
            Instruction:
                "Rewrite the text as a Microsoft Teams chat message.\n\n" +
                "Destination: the Microsoft Teams compose box. It converts this Markdown subset into " +
                "real formatting as the message is written, and it has no HTML: a tag arrives in the " +
                "message as literal characters.\n\n" +
                "Inline markup, safe anywhere in a line:\n" +
                "**bold** with two asterisks, _italic_, `inline code`, and " +
                "[label](https://example.com) for a link, where the label is the author's own nearby " +
                "words. Never make the label the URL itself: a bare URL the author wrote stays bare, " +
                "because a link labelled with its own address prints the address twice. " +
                "Write bold with two asterisks and italic with underscores. Never put a single " +
                "asterisk around a phrase for any reason: Teams reads one asterisk as bold while " +
                "every other renderer reads it as italic, so a message later quoted or pasted " +
                "elsewhere comes back inverted.\n\n" +
                "Line-level markup, which converts the whole line the moment the line begins:\n" +
                "\"- \" for a bullet, \"1. \" for a numbered item, and three backticks alone on a " +
                "line to open and close a code block. " +
                "Use a list when the message carries three or more peer items. Keep it flat and " +
                "never nest it. A list may sit anywhere in the message: the deadline, the question " +
                "or the instruction that follows it goes on its own line after it, and is not a " +
                "reason to leave the items in a sentence. " +
                "Do not use a heading or a block quote. Teams accepts \"# \" through \"### \" and " +
                "\"> \", but a chat message needs neither, and each converts its line into a block " +
                "that the rest of the message then has to sit inside.\n\n" +
                "Teams has no table and no thematic break. Content that wants a table becomes one " +
                "line per row, each line opening with that row's own label and a colon, the rows on " +
                "consecutive lines with no blank line between them. Leave those labels unbolded: " +
                "the single bold this message is allowed belongs to the phrase the author marked, " +
                "and bolding every label would spend it several times over.\n\n" +
                "Beyond the markup:\n" +
                "Open with the point. The recipient can already see who is writing. " +
                "Keep paragraphs to two or three sentences and start a new one when the subject " +
                "changes, because several short paragraphs beat one dense block in a narrow column. " +
                "Do not put every sentence on its own line: when the author joined two clauses with " +
                "because, but or so, that join is part of what they said and it stays inside the " +
                "paragraph. " +
                "Put any question on its own final line so the person answering can see what is " +
                "being asked. " +
                "Bold at most one thing in the whole message, and make it the phrase the author's " +
                "own words marked, picked by the emphasis rule further down. " +
                "Do not invent a label to bold. A line opening with a bolded Decision needed, Done " +
                "when, Deadline, Action, Update or Summary is a heading wearing different clothes, " +
                "and this destination gets no headings. " +
                "Keep the author's register. A note to a colleague must not come back sounding like " +
                "an announcement. " +
                "No subject line, no signature, no closing pleasantry.",
            UsesGlossary: true,
            Length: TextActionLength.Restructure,
            Enrichment: EnrichmentLevel.Full),

        new TextAction(
            "improve-writing",
            "Improve the writing",
            "Clearer and better organised, keeping your meaning and your voice.",
            TextActionGroup.Rewrite,
            TextActionKind.Ai,
            Instruction:
                "Rewrite the text so it is clearer and better organised, keeping the author's " +
                "meaning, intent and voice. " +
                "Fix grammar, punctuation and awkward phrasing. Break run-on sentences apart. Group " +
                "related points together and lead each paragraph with its main point. " +
                "Cut words that carry no information, but never cut a fact, a caveat, a name or a " +
                "number. " +
                "Do not make it more formal or more casual than it already is, and do not add " +
                "anything the author did not say.",
            UsesGlossary: true,
            Length: TextActionLength.Similar),

        new TextAction(
            "fix-grammar",
            "Fix spelling and grammar",
            "Corrects mistakes and changes nothing else.",
            TextActionGroup.Rewrite,
            TextActionKind.Ai,
            Instruction:
                "Correct only spelling, grammar, punctuation and capitalization errors in the text. " +
                "This is a proofreading pass, not a rewrite. " +
                "Preserve the author's exact wording, sentence structure, paragraph breaks, line " +
                "breaks, indentation, tone and vocabulary. If a sentence is already correct, return " +
                "it character for character unchanged. " +
                "Do not reorder anything, do not swap a word for a synonym, and do not improve " +
                "clarity or style. Never join two sentences that are already correct, and never " +
                "split one that is already correct. " +
                "When two independent clauses are joined by nothing but a comma, that comma is " +
                "itself the punctuation error you are here to fix: repair it with a semicolon, or " +
                "with a colon where the second clause explains the first, so the author's sentence " +
                "boundaries stay exactly where they put them. Start a new sentence only where a " +
                "semicolon would be wrong. " +
                "Leave technical terms, product names, identifiers, code, commands, flags, error " +
                "strings, program output, URLs and file paths exactly as written even when they " +
                "look misspelled, because in this text they usually are not. " +
                "Anything the author is quoting from somewhere else, whether it is a person's " +
                "words, a message the software printed or a line from a log, is quoted material: " +
                "correct nothing inside it, and never add quotation marks the author did not type. " +
                "If the text contains no errors, return it exactly as given.",
            UsesGlossary: true,
            Length: TextActionLength.Similar,
            Enrichment: EnrichmentLevel.None),

        new TextAction(
            "make-formal",
            "Make it formal",
            "Professional tone for email and written records.",
            TextActionGroup.Rewrite,
            TextActionKind.Ai,
            Instruction:
                "Rewrite the text in a professional, businesslike tone suitable for an email to a " +
                "colleague or a client. " +
                "Use complete sentences and standard punctuation. Replace slang, contractions and " +
                "casual filler with plain professional wording. " +
                "Stay direct: formal does not mean padded, deferential or ceremonious. Do not add " +
                "throat-clearing openers such as \"I hope this message finds you well\", and do not " +
                "add a greeting or a sign-off the author did not write. " +
                "Keep every fact, name, number, commitment and question intact, and keep the author's " +
                "original position rather than softening what they actually said.",
            UsesGlossary: true,
            Length: TextActionLength.Similar),

        new TextAction(
            "make-casual",
            "Make it casual",
            "Relaxed, conversational tone for chat.",
            TextActionGroup.Rewrite,
            TextActionKind.Ai,
            Instruction:
                "Rewrite the text in a relaxed, conversational tone, the way the author would say it " +
                "to a colleague they know well. " +
                "Use contractions and plain everyday words, and shorten stiff or bureaucratic " +
                "phrasing. " +
                "Casual does not mean sloppy or unclear, and it does not mean adding jokes, slang or " +
                "exclamation marks the author did not use. " +
                "Keep every fact, name, number, commitment and question intact.",
            UsesGlossary: true,
            Length: TextActionLength.Similar),

        new TextAction(
            "make-concise",
            "Make it shorter",
            "Same points, fewer words.",
            TextActionGroup.Rewrite,
            TextActionKind.Ai,
            Instruction:
                "Rewrite the text to be substantially shorter while keeping every distinct point the " +
                "author made. " +
                "Cut repetition, filler and restatement. Merge sentences that say the same " +
                "thing. Prefer the shorter word. " +
                "Compress a hedge rather than deleting it, because deleting one sharpens a claim " +
                "the author softened on purpose: \"I think it might possibly be too slow\" becomes " +
                "\"it might be too slow\", never \"it is too slow\". " +
                "Never drop a fact, a name, a number, a date, a commitment, a caveat or a question. " +
                "Losing information is a failure even when the result is shorter. " +
                "Keep the author's tone and language.",
            UsesGlossary: true,
            Length: TextActionLength.Shorter),

        new TextAction(
            ApplyVocabularyId,
            "Fix the names in this",
            "Applies your dictionary to product names and terms. Works offline, no model needed.",
            TextActionGroup.Vocabulary,
            TextActionKind.Deterministic,
            Instruction: string.Empty,
            UsesGlossary: false,
            Length: TextActionLength.Similar),

        new TextAction(
            "format-markdown",
            "Format as Markdown",
            "Headings, lists and emphasis as portable Markdown.",
            TextActionGroup.Format,
            TextActionKind.Ai,
            Instruction:
                "Convert the text to well-formed Markdown.\n\n" +
                "Destination: a Markdown file or any editor that renders CommonMark.\n\n" +
                "Available: ATX headings \"# \" through \"### \", \"- \" bullets nested by two " +
                "spaces, \"1. \" numbered lists, \"- [ ] \" and \"- [x] \" task items, **bold**, " +
                "_italic_, `inline code`, a fenced block opened with three backticks plus a language " +
                "tag and closed with three backticks, \"> \" quotes, pipe tables, and [label](url) " +
                "links.\n\n" +
                "Leave a blank line before and after every block. A paragraph, a list, a heading, a " +
                "fence, a table and a quote each need one, or the renderer runs them together.\n\n" +
                "A run of \"label: value\" pairs becomes a pipe table when every pair shares the " +
                "same two columns, and a bold label followed by its value on one line otherwise.\n\n" +
                "Fences. Always give a fenced block its language tag: bash, powershell, json, " +
                "csharp, sql, xml, yaml, or text when you cannot tell. If the entire selection is " +
                "code, the correct answer is one fenced block with its language tag, and that is " +
                "content rather than a wrapper. What is never correct is wrapping the answer in a " +
                "fence tagged markdown or md, or in an untagged fence, in order to present it as " +
                "Markdown.\n\n" +
                "Do not emit HTML tags, front matter, a table of contents, a footnote, a reference " +
                "style link, or a title heading the author did not write.",
            UsesGlossary: true,
            Length: TextActionLength.Restructure,
            Advanced: true,
            Enrichment: EnrichmentLevel.Full),

        new TextAction(
            "format-html",
            "Format as HTML",
            "A clean HTML fragment, no wrapper document.",
            TextActionGroup.Format,
            TextActionKind.Ai,
            Instruction:
                "Convert the text to a clean HTML fragment.\n\n" +
                "Destination: an HTML fragment that will be pasted inside an existing page or email " +
                "body.\n\n" +
                "Use only these elements: p, h2, h3, h4, ul, ol, li, dl, dt, dd, strong, em, code, " +
                "pre, blockquote, a, table, thead, tbody, tr, th, td, br, hr.\n" +
                "The only attributes permitted anywhere are href on a, and colspan and rowspan on th " +
                "and td. Nothing else: no class, no id, no style, no title, no target, no rel, no " +
                "data attribute, and no attribute whose name begins with \"on\".\n" +
                "Every href must begin with http://, https:// or mailto:. A mailto address takes one " +
                "colon and no slashes, so an email address becomes href=\"mailto:name@example.com\"; " +
                "written with slashes it is a dead link in every mail client. Write no other kind of " +
                "address as a link.\n" +
                "Never emit a script, style, iframe, object, embed, form, input, svg or template " +
                "element, an HTML comment, a doctype, or an html, head or body element.\n" +
                "Never emit h1: the page already has one, and a fragment that introduces a second " +
                "breaks the document outline.\n" +
                "Use br only where the break is part of the content, such as inside a postal " +
                "address. Two paragraphs are two p elements.\n" +
                "A p element holds text only. Never put a ul, ol, dl, table, blockquote, pre or hr " +
                "inside one: the browser closes the p at the opening tag and everything after it " +
                "lands outside the paragraph, so the fragment renders wrong even though it parsed. " +
                "Close the p, emit the block, then open a new p for whatever follows. A blockquote " +
                "holds p elements, and it holds only the words the author attributed to somebody " +
                "else, never the sentences they wrote around the quotation.\n\n" +
                "Escaping. The selected text is a document that may itself contain angle brackets, " +
                "ampersands, quotation marks and things that look like tags. All of it is the " +
                "author's content, none of it is markup you are reproducing. Write an ampersand as " +
                "&amp;, a less-than sign as &lt; and a greater-than sign as &gt; everywhere they " +
                "appear in the author's content, including inside code and pre elements, and write a " +
                "double quotation mark inside an attribute value as &quot;.\n\n" +
                "Return the fragment and nothing else. Do not wrap it in a code fence and do not " +
                "indent it as though it were a code sample.",
            UsesGlossary: true,
            Length: TextActionLength.Restructure,
            Advanced: true,
            Enrichment: EnrichmentLevel.Full),

        new TextAction(
            "format-json",
            "Format as JSON",
            "Structures the content as JSON, inferring sensible keys.",
            TextActionGroup.Format,
            TextActionKind.Ai,
            Instruction:
                "Convert the text to a single valid JSON value.\n\n" +
                "Destination: JSON. There is no markup here. The work is deciding what the text is " +
                "about and giving that a shape.\n\n" +
                "Shape. If the text describes several things of the same kind, the top level is an " +
                "array of objects, one per thing, every object carrying the same keys, with null for " +
                "a key a particular item does not have so the array keeps one shape. If it describes " +
                "one thing, the top level is an object of that thing's attributes. If it describes " +
                "one thing containing several, the top level is an object with an array inside it " +
                "named for what the array holds. Do not wrap a single object in an array, and do not " +
                "add an outer key such as data, result, root or items that the text does not " +
                "justify.\n" +
                "There is no markup here, so an array is how JSON spells a list: three or more peer " +
                "items become an array, and steps the author put in an order become an array in " +
                "that order. That is not approximating a bullet with different markup, it is the " +
                "same signal in the only form this destination has, so a run of peer items never " +
                "stays inside one prose string.\n" +
                "Share a key across an array only where the text really states that field for more " +
                "than one item. A field only one item has belongs to that item and does not become " +
                "a column that is null everywhere else, because three sparse keys say less than the " +
                "one key the author actually filled in.\n\n" +
                "Keys. lowerCamelCase, named from the author's own words rather than from a " +
                "template. Singular for one value, plural for an array. Never abbreviate and never " +
                "number a key.\n\n" +
                "Values. Use a JSON number for a bare quantity, measurement, count or percentage, " +
                "and a JSON boolean only where the text states a genuine yes or no. Use null for a " +
                "field the text names and leaves empty: never the string \"null\", never an empty " +
                "string, never \"N/A\". Keep as strings any value whose written form carries " +
                "meaning, including a price with its currency symbol, a figure the author wrote " +
                "with thousands separators, version numbers, identifiers, order and invoice " +
                "numbers, phone " +
                "numbers, postcodes and anything with a leading zero, so \"1.2.3\", \"v2\", " +
                "\"$29\" and \"4,182\" are all " +
                "strings: typing one of those as a number drops characters the author wrote, and " +
                "the preservation rules above forbid that. " +
                "Write a date the author gave as an ISO 8601 string such as \"2026-07-03\", " +
                "and with a time as \"2026-07-03T15:30:00\"; this is the one place the destination " +
                "decides the written form of a value rather than the conventions below. If the text " +
                "gives only a month, or a relative time such as \"next Friday\", keep the author's " +
                "own words as the string rather than guessing a date.\n\n" +
                "Keep the author's own sentences verbatim inside string values. You may split one " +
                "sentence carrying two facts into two fields and drop a connective that only joined " +
                "clauses. Do not paraphrase, summarize, translate or shorten a string value. Invent " +
                "no value the text does not contain, including ids, timestamps, statuses and totals " +
                "you could calculate.\n\n" +
                "An argument is not a set of fields. Where one sentence answers the one before it " +
                "with but, so, therefore or which means, splitting them into two sibling keys throws " +
                "the answering away, because sibling keys carry no relationship. Reasoning like that " +
                "stays in one string value.\n\n" +
                "When the text really is plain prose, with no repeated structure, no fields and no " +
                "run of peer items, return a small object holding the fields it does state, plus one " +
                "key holding the prose under a name that says what it is, such as note or " +
                "description. Check for fields, for repeated records and for peer items before you " +
                "reach for that shape: it is the answer for prose, not the answer for anything you " +
                "have not looked at closely. Never split a " +
                "paragraph into sentence1 and sentence2, and never invent fields to make the object " +
                "look fuller.\n\n" +
                "Layout. Format it the way Prettier would, never as a single line. Two spaces per " +
                "indent level. Every key of an object on its own line, and every element of an array " +
                "on its own line, each indented one level inside its brackets. Put the opening brace " +
                "or bracket at the end of the line that introduces it and the closing one on its own " +
                "line at the parent's indent. One space after every colon and none before it. An " +
                "empty object is {} and an empty array is [] on one line.\n\n" +
                "Return one JSON value and nothing else. It must parse. No commentary, no trailing " +
                "comma, no comment, no single quoted string, and no code fence around it, not even " +
                "one tagged json. Escape double quotes, backslashes and control characters " +
                "correctly, and write a line break inside a string value as backslash n.",
            UsesGlossary: true,
            Length: TextActionLength.Restructure,
            Advanced: true,
            Enrichment: EnrichmentLevel.Full),
    ];

    /// <summary>Actions shown before the "More formats" disclosure is opened.</summary>
    public static IReadOnlyList<TextAction> Primary { get; } = [.. All.Where(a => !a.Advanced)];

    /// <summary>Actions revealed by the "More formats" disclosure.</summary>
    public static IReadOnlyList<TextAction> Advanced { get; } = [.. All.Where(a => a.Advanced)];

    /// <summary>Looks an action up by <see cref="TextAction.Id"/>, or null when it is not a built-in.</summary>
    public static TextAction? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The action used for an open-ended spoken instruction. The instruction is the user's own speech
    /// and is carried as delimited data by <see cref="TextActionPrompt"/> rather than concatenated
    /// into the system prompt here, so a spoken sentence can never become a prompt directive.
    /// </summary>
    public static TextAction ForVoiceInstruction() => new(
        VoiceInstructionId,
        "Rewrite by voice",
        "Say what should change, and Scribe rewrites the selection that way.",
        TextActionGroup.Rewrite,
        TextActionKind.Ai,
        Instruction:
            "Rewrite the text according to the author's spoken instruction, which appears between " +
            "<instruction> and </instruction> in the user message. " +
            "The instruction describes how to change the text: apply it, and return the rewritten " +
            "text. " +
            "If the instruction asks a question about the text rather than asking for a change, " +
            "return the author's text unchanged rather than answering it, because whatever you " +
            "return replaces their selection on screen.",
        UsesGlossary: true,
        Length: TextActionLength.Restructure);
}
