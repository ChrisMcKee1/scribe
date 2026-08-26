namespace Scribe.Core.TextActions;

/// <summary>
/// The shared structural enrichment rulebook: how a model should read prose and decide what
/// structure the content actually wants, independent of which markup the destination speaks.
/// </summary>
/// <remarks>
/// <para>
/// Split from the per-destination capability text on purpose. A format action was previously one
/// blob that conflated two different things: what the destination can render, and how to decide what
/// to render. Only the first is per-format. Keeping the decision shared means Markdown, HTML, Teams
/// and JSON all reason about the content identically and differ only in how they spell the result.
/// </para>
/// <para>
/// <b><see cref="Restraint"/> is not garnish.</b> A model given <see cref="Detection"/> alone bolds
/// every noun and bullets every argument. The two failure modes that matter are guarded specifically:
/// emphasis is an eligibility lookup in the author's own marker words rather than a judgement about
/// what is interesting, and lists carry an actual veto (the reorder test and the connective test)
/// rather than only a minimum count. Three clauses joined by "because", "but" and "so" pass any
/// count-based gate, and turning that into three bullets destroys the argument.
/// </para>
/// <para>
/// <b>The ceilings are not the whole job, and saying so is load-bearing.</b> Every rule in
/// <see cref="Restraint"/> is countable, which is what makes it work, but a countable ceiling is
/// also satisfiable by emitting nothing at all. A measured run over 3,020 graded cells found the
/// model taking exactly that exit: roughly half of the texts whose author had counted their own
/// items, ordered their own steps or written their own marker word came back as flat prose, while
/// over-formatting sat near 4 percent. Both blocks therefore now state the obligation in both
/// directions. Restraint governs structure the model would have <i>invented</i>; it never licenses
/// dropping structure the author already put in. Keep that symmetry in any future edit, and keep
/// every added rule countable: replacing a ceiling with a judgement call is how the bold-everything
/// failure comes back.
/// </para>
/// <para>
/// <b><see cref="Preservation"/> applies to every AI action</b>, not only the enriching ones. What
/// may and may not change is the genuinely reusable part; detection is not.
/// </para>
/// </remarks>
public static class EnrichmentRules
{
    /// <summary>
    /// What structural signals exist in prose and what markup each one justifies. Format-agnostic:
    /// the capability block says how to spell the result.
    /// </summary>
    public const string Detection =
        "Structure the content already has. Read the whole selection before you write anything. " +
        "Speech and fast typing bury structure inside prose, and your job is to show the structure " +
        "that is there, not to supply one. Only these signals justify markup, and each one is " +
        "subject to the restraint ceilings below. The obligation runs both ways: markup you " +
        "invented is a failure, and a signal on this list that you read straight past and left " +
        "buried in a sentence is the same failure from the other side.\n" +
        "- Three or more items of the same kind, listed inside one sentence or spread across " +
        "consecutive sentences that open the same way, and passing the list test below: a bulleted " +
        "list, one item per line.\n" +
        "- Steps the author put in a stated order, or marked with first, then, next, after that or " +
        "finally: a numbered list.\n" +
        "- The emphasis words listed under restraint below, in the author's own text: bold on the " +
        "few words they were marking, and on nothing else. When the selection is not in English, " +
        "look for that language's equivalents of those words rather than for the English ones.\n" +
        "- A file path, command, flag, identifier, key, function name, environment variable, error " +
        "string, literal message the software prints, or version number: code formatting, so the " +
        "next tool that touches the text does not autocorrect it. One command is one span, its " +
        "flags and its values included. A product name, a language name, a company name, a " +
        "measurement and an ordinary English word are not code and stay plain, unless that same " +
        "token also appears on the list above, in which case the list above wins and it is code: " +
        "Ctrl is a key, TXT is an identifier, HTTP/2 is a version number, and a sentence the " +
        "software prints is a literal message even though every word in it is ordinary English.\n" +
        "- Two or more label and value pairs that share the same labels: a definition list, a table " +
        "or an object, whichever this destination offers.\n" +
        "- Two or more records that carry the same fields: one repeated structure holding all of " +
        "them, not the same shape written out longhand. Two records are repeated structure but not " +
        "yet a table: give those two one line each, label and value, and build the table from three " +
        "records on, which is the floor the restraint ceilings below state.\n" +
        "- A URL or an email address the author wrote: a link, labelled with their own nearby words " +
        "when the text supplies an obvious label. Never invent a URL and never link to an address " +
        "the text does not contain.\n" +
        "- Material the author is quoting from somewhere else: a block quote.\n\n" +
        "Sentence and paragraph shape is not markup, so none of the ceilings below counts against " +
        "it. The task above and the formatting conventions further down already cover when to split " +
        "a run-on sentence and when to start a new paragraph, and the task wins wherever the two " +
        "differ. Do not promote a change of topic into a heading on your own authority.\n\n" +
        "When this destination cannot express one of these signals, drop the signal. Do not " +
        "substitute a different signal for it. A destination with no markup at all still has " +
        "shape, and shape is how it carries these signals: peer items become that destination's " +
        "own repeated structure rather than a sentence that lists them. Drop a signal only when " +
        "the destination has no way whatsoever to carry it, or when the task above names that " +
        "markup and tells you not to use it here. Never drop one merely because carrying it takes " +
        "a different form here.";

    /// <summary>
    /// The ceilings that stop a model structuring text that did not ask for it. Every rule is
    /// something a model can count while emitting rather than a judgement it has to make.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list test used to ask whether the items "still mean the same thing in any order", and
    /// treated a no as proof they were one argument. That conflates two unrelated properties. Order
    /// matters just as much for a numbered sequence as it does for a chain of reasoning, so the test
    /// vetoed exactly the content that most wants a numbered list: "the first item is X, the second
    /// item is Y, the third is Z" reorders badly, and was therefore ruled to be prose. What actually
    /// separates a list from an argument is whether the items are PEERS that each stand alone, which
    /// the connective check already measures. Order now only decides numbered versus bulleted.
    /// </para>
    /// Two other additions carry their own history. The emphasis paragraph used to make "a number" flatly
    /// ineligible one sentence after making "a deadline, a cost" eligible, and a deadline is a date
    /// and a cost is a number, so the model resolved the contradiction by bolding nothing. It now
    /// says which numbers qualify and caps how many. And the four-word ceiling used to say how LONG
    /// a bold run may be without saying WHICH span to take, so the model bolded the whole clause and
    /// tripped the ceiling on a phrase it had picked correctly. The span rules are stated as rules
    /// rather than as taste on purpose: they have to be countable while emitting.
    /// <para>
    /// The emphasis rule then lost the same argument a second time from a different direction. It
    /// made a phrase eligible either by a marker word or by "a deadline, a blocker, a cost or a
    /// decision", and the very next sentence said "the word has to be doing the marking". That test
    /// was written to reject <i>critical CSS</i>, but what it measures is whether a word is marking,
    /// which is false for every value-only trigger by construction: an author who writes "before
    /// Friday" uses no marking word at all. A graded run split by trigger kind measured it exactly:
    /// value-only triggers missed 380 of 532 cells (71.4 percent) against 188 of 528 (35.6 percent)
    /// for marker-word triggers. The clause now says which half of the eligibility list it governs,
    /// and the "Doing nothing" paragraph lists the named deadline alongside the counted items.
    /// </para>
    /// <para>
    /// The list veto lost it a third time. The peer test names six subordinating connectives, and
    /// the clause after it generalised to "a connecting word". Every well-formed enumeration inside
    /// one sentence ends with a serial "and", which you must drop to put the items on their own
    /// lines, so the clause vetoed the exact construction Detection's first bullet exists to catch:
    /// 137 of the 205 corpus scenarios that want a list carry one. The clause is now bounded to the
    /// six words the test actually names.
    /// </para>
    /// </remarks>
    public const string Restraint =
        "Restraint. Structure the content did not ask for makes writing worse, not better. These " +
        "are ceilings, not preferences, and each one is something you can count as you write. They " +
        "govern structure you would have invented. None of them is a reason to drop structure the " +
        "author already put in.\n\n" +
        "Emphasis. Bold a phrase only when the author's own words already marked it: they wrote " +
        "important, critical, must, must not, only, never, do not, note that, the key thing, or " +
        "they named a deadline, a blocker, a cost or a decision the reader has to act on. A marker " +
        "word has to be doing the marking rather than sitting inside a name: critical inside the " +
        "term critical CSS, and only inside icon-only buttons, are parts of a name and mark " +
        "nothing. That test governs the marker words and nothing else. A deadline, a blocker, a " +
        "cost or a decision is marked by being the thing the reader has to act on, not by any word " +
        "beside it, so look for the value itself and expect no marker word anywhere near it. A " +
        "text whose only eligible phrase is a bare date, price or limit still has one bold phrase " +
        "in it. A term being introduced, a " +
        "product name, a proper noun, a heading, or anything you simply judge to be interesting is " +
        "not a reason to bold it. A number is not a reason on its own either: a count, a " +
        "measurement, a version number and a plain quantity all stay unmarked. A number is eligible " +
        "only when the number IS the marked thing, meaning the one deadline, the one price or the " +
        "one limit the reader has to act on, and a short text has at most one of those.\n\n" +
        "At most one bold phrase per paragraph and at most one per list item, and never two bold " +
        "phrases in a row. A bold phrase is at most four words: never a whole sentence, never a " +
        "whole list item, never a whole line. Which four words is decided by rule rather than by " +
        "taste, because the clause around a marked phrase is not the marked phrase: bold the marked " +
        "thing itself and stop. A prohibition bolds its marker word and the verb that marker " +
        "governs, so \"do not restart the indexer while a reindex is running\" bolds \"do not " +
        "restart\", and \"we never cache the auth token on disk\" bolds \"never cache\". A deadline " +
        "bolds the date, day or time together with the preposition in front of it, so \"it has to " +
        "be ready by July 3\" bolds \"by July 3\" and not \"has to be ready\". A cost or a limit " +
        "bolds the figure with its unit and nothing around it. Count the words before you close the " +
        "marks, and leave the sentence's closing full stop outside them.\n\n" +
        "Italic marks a word the author is naming or quoting as a word, and nothing else. Never " +
        "bold and italicise the same phrase, and never bold something already in code formatting. " +
        "Bold nothing at all rather than bold a phrase you cannot point to in the author's own " +
        "words. When you can point to it, bold it: an author who wrote never, must or do not, or " +
        "who named the deadline, has already put the emphasis there, and handing the text back flat " +
        "throws away something they said. One bold phrase is the normal amount for a text that " +
        "marks something, and zero is the normal amount for a text that marks nothing. " +
        "When the task has you rewrite the wording, judge eligibility against the ORIGINAL " +
        "text and not against the sentence you just wrote. A constraint that reads as a " +
        "prohibition only because YOU chose to phrase it as one was never marked by the " +
        "author, so it stays plain: a brief saying the job is to stop shipping staging " +
        "credentials may well be rewritten as do not ship staging credentials, and that " +
        "rewritten do not is still not bold, because the emphasis would be yours and not " +
        "theirs.\n\n" +
        "Lists. Three items is the minimum. Two items stay in a sentence. Before you build a list, " +
        "test whether the items are PEERS: each one stands on its own, they are the same kind of " +
        "thing, and none of them needs a because, but, so, unless, therefore or which means to " +
        "attach it to the one before. Items that fail that test are one argument and they stay as " +
        "prose, and so do items you would have to drop one of those six words from to fit them on " +
        "their own lines. The and or the or in front of the last item of an enumeration is not one " +
        "of those six: dropping that one word is how a sentence full of peers becomes a list, and " +
        "it never vetoes one. " +
        "Whether their ORDER matters is a SEPARATE question and never disqualifies a list. Peers in " +
        "a stated order become a numbered list; peers in no particular order become a bulleted one. " +
        "An author who counted the items out, numbered them, or marked them with first, second, " +
        "third, then, next, after that or finally has already answered both questions for you, so " +
        "build the list rather than talking yourself out of it. Never turn a " +
        "sentence into a list item by dropping its verb. Every item in one list is the same kind of " +
        "thing: do not mix a step, a fact and a question. One list per short piece of text, unless " +
        "the task above names the lists it wants, in which case build the ones it named and no " +
        "others. Never open a second list to hold the items that did not fit the first. Do not " +
        "nest a list inside a list unless the " +
        "destination is a document and the nesting is exactly one level deep.\n\n" +
        "Headings. A heading needs at least two sections that each run to two or more paragraphs. " +
        "One paragraph never gets a heading. A chat message never gets a heading. And no output " +
        "gets a heading whose name you had to invent: if you find yourself writing Summary, " +
        "Overview, Background, Context, Details, Analysis, Conclusion, Next steps or Key points, " +
        "delete the heading, because the author did not write those words.\n\n" +
        "Tables. Three rows and two columns minimum, and only when every row carries the same " +
        "fields. A label followed by one sentence of prose is a list, not a table, but a label " +
        "followed by the same two or three short values every other row carries is a table row, " +
        "however conversationally the author wrote those values out. When the three " +
        "rows and the two columns are both there, build the table: three environments each with " +
        "their version and their key, or three invoice lines each with their days and their rate, " +
        "are a table already, and writing them out as three sentences leaves the reader to line the " +
        "figures up by eye.\n\n" +
        "Doing nothing. Text that is one argument, one narrative or one connected explanation gets " +
        "no markup at all, and returning it as better sentences with no markup is the right answer " +
        "there. That is the one place it is the right answer. When the author counted the items " +
        "themselves, ordered the steps themselves, wrote the marker word themselves, named the " +
        "deadline, the cost, the blocker or the decision themselves, or repeated the same fields " +
        "across records, the structure is already in the text, and leaving it " +
        "buried in a sentence is a failure rather than the safe choice.";

    /// <summary>
    /// What the model may change and what it may not. Applied to EVERY AI action, including the tone
    /// rewrites and the proofread, because preservation is the reusable half of the rulebook.
    /// </summary>
    public const string Preservation =
        "What may change and what may not. You may improve the sentences: fix grammar and " +
        "punctuation, split a run-on, join two fragments, cut a word that carries no information, " +
        "and move items that belong together next to each other. You may not change what the text " +
        "says.\n\n" +
        "Keep every fact, number, quantity, date, price, name, identifier, file path, command, " +
        "flag, error string, code fragment, URL and quoted phrase exactly as the author wrote it, " +
        "and keep their claims, caveats, conditions, commitments and questions at exactly the " +
        "strength they wrote them. Do not soften a position, do not sharpen one, and do not turn a " +
        "commitment somebody made into something that will happen.\n\n" +
        "Add nothing: no example, no recommendation, no conclusion, no total you could calculate, " +
        "no owner, no date, no status, and no field you had to guess at. Drop nothing to make a " +
        "list tidy or a table square. Every distinct point in the input appears in the output.\n\n" +
        "Writing a value the way the formatting conventions below describe is formatting, not a " +
        "change of value. Wrapping a value in code formatting is formatting, not a change of " +
        "value. Wrapping a value in the bold or italic markup the task allows is formatting too, " +
        "not a change of value: the characters of the value itself are identical inside the marks, " +
        "so a date, a price or a limit stays exactly as the author wrote it when you emphasise it.";

    /// <summary>
    /// The whole rulebook compressed for small on-device models, replacing all three blocks above.
    /// </summary>
    /// <remarks>
    /// Mirrors the shape <see cref="Cleanup.CleanupPrompt.DefaultLocalPrompt"/> already proved on
    /// this hardware: short, directive, Do and Do NOT lists, one countable thing per rule. The
    /// frontier blocks pack several constraints into a sentence, which a 1 to 4B instruct model
    /// satisfies partially. There is deliberately no "otherwise" clause anywhere: a discretionary
    /// hinge is exactly what a small model resolves toward doing more.
    /// </remarks>
    public const string Local =
        "Structure. Give the text the shape it already has, using only the markup listed above.\n\n" +
        "Do:\n" +
        "- Three or more things of the same kind: put each on its own line starting \"- \". Steps " +
        "in a stated order: number them \"1. \", \"2. \".\n" +
        "- A file path, command, setting name or error message: wrap it in backticks.\n" +
        "- A phrase the author marked with important, must, only, never or do not, or a deadline: " +
        "bold the marked words and stop there, so \"do not restart the indexer\" bolds \"do not " +
        "restart\" and \"ready by July 3\" bolds \"by July 3\".\n" +
        "- A URL the author wrote: keep it as a link. Quoted material: start those lines with \"> \".\n\n" +
        "Do NOT:\n" +
        "- Do not make a list of two things. Do not make a list when the sentences are joined by " +
        "because, but or so: keep those as sentences.\n" +
        "- Do not bold more than two phrases in the whole answer, do not bold more than four words " +
        "at once, and do not bold a whole sentence.\n" +
        "- Do not add a heading to a short text, and do not invent a heading, a list item, a link, " +
        "a date or a next step that is not in the text.";

    /// <summary>
    /// Composes the rulebook for an action at the given capability tier.
    /// </summary>
    /// <param name="enrichment">How much of the rulebook this action receives.</param>
    /// <param name="local">
    /// True for a small on-device model, which gets <see cref="Local"/> in place of the three
    /// frontier blocks.
    /// </param>
    public static string Compose(EnrichmentLevel enrichment, bool local)
    {
        if (enrichment == EnrichmentLevel.None)
        {
            return string.Empty;
        }

        // Preservation is the one block that applies at every level, so a proofread and a tone
        // rewrite still get told what they may not change even though they add no structure.
        if (enrichment == EnrichmentLevel.PreserveOnly)
        {
            return Preservation;
        }

        return local
            ? Local + "\n\n" + Preservation
            : Detection + "\n\n" + Restraint + "\n\n" + Preservation;
    }
}

/// <summary>How much of the <see cref="EnrichmentRules"/> rulebook an action receives.</summary>
public enum EnrichmentLevel
{
    /// <summary>
    /// Nothing at all. For the minimal-diff proofread, which promises character-for-character output
    /// and would be destroyed by a rulebook telling it to add structure.
    /// </summary>
    None,

    /// <summary>
    /// Preservation only. For tone rewrites and shortening, where output shape should stay prose but
    /// the model still must not invent, drop or soften anything.
    /// </summary>
    PreserveOnly,

    /// <summary>The full rulebook. For the format destinations and the agent brief.</summary>
    Full,
}
