using System.Text;
using System.Text.Json;
using Scribe.Core.TextActions;
using Scribe.StyleEval.Corpus;
using Scribe.StyleEval.Markup;

namespace Scribe.StyleEval.Checks;

/// <summary>
/// Everything the deterministic checkers need about one scenario-plus-action cell, parsed once.
/// </summary>
/// <remarks>
/// Parsing is done here rather than inside each checker because eleven checkers run over the same
/// answer and three of them want a real parse tree. Doing it once per cell keeps a ten thousand cell
/// run cheap, and it means a parse failure is reported identically by every checker that depended
/// on it.
/// </remarks>
internal sealed class CheckContext
{
    private MarkupFacts? _markup;
    private MarkupFacts? _inputMarkup;
    private HtmlFragmentFacts? _html;
    private JsonDocument? _json;
    private bool _jsonParsed;
    private string? _searchSurface;

    public CheckContext(Scenario scenario, TextAction action, string output, bool sanitizerAccepted)
    {
        Scenario = scenario;
        Action = action;
        Output = output ?? string.Empty;
        SanitizerAccepted = sanitizerAccepted;
        Destination = Destinations.For(action.Id);
    }

    public Scenario Scenario { get; }

    public TextAction Action { get; }

    /// <summary>The text being graded: the sanitized answer, or the raw answer when it was rejected.</summary>
    public string Output { get; }

    /// <summary>False when <c>TextActionSanitizer</c> refused the answer.</summary>
    public bool SanitizerAccepted { get; }

    public Destination Destination { get; }

    /// <summary>Structural facts, normalised across Markdown, Teams and HTML.</summary>
    public MarkupFacts Markup => _markup ??= Destination switch
    {
        Destination.Html => Html.ToMarkupFacts(),
        Destination.Json => MarkupFacts.Unreadable("JSON has no markup layer"),
        _ => MarkdownReader.Read(Output),
    };

    /// <summary>
    /// The same structural read of the SELECTION, so a restraint ceiling can tell markup the model
    /// added from markup the author already had.
    /// </summary>
    /// <remarks>
    /// Without this the restraint checkers punish preservation. A selection that is already Markdown
    /// containing a two-item list comes back from the proofread with that two-item list intact,
    /// which is exactly right, and a naive "no list under three items" reads it as over-formatting.
    /// The ceilings govern structure the model INTRODUCED.
    /// </remarks>
    public MarkupFacts InputMarkup => _inputMarkup ??= MarkdownReader.Read(Scenario.Text);

    /// <summary>The strict HTML fragment parse. Only meaningful for the HTML destination.</summary>
    public HtmlFragmentFacts Html => _html ??= HtmlFragment.Parse(Output);

    /// <summary>The parsed JSON answer, or null when it does not parse.</summary>
    public JsonDocument? Json
    {
        get
        {
            if (_jsonParsed)
            {
                return _json;
            }

            _jsonParsed = true;
            try
            {
                _json = JsonDocument.Parse(Output);
            }
            catch (JsonException)
            {
                _json = null;
            }

            return _json;
        }
    }

    /// <summary>
    /// Every rendering of the answer a protected token could legitimately survive into: the literal
    /// output, plus the destination's decoded form.
    /// </summary>
    /// <remarks>
    /// A URL kept byte-identical inside an HTML fragment arrives as <c>&amp;amp;</c> where the author
    /// wrote <c>&amp;</c>, and inside JSON a backslash arrives doubled. Both are correct preservation
    /// and neither survives a naive substring test on the raw answer, so the decoded surface is
    /// searched too.
    /// </remarks>
    public string SearchSurface
    {
        get
        {
            if (_searchSurface is not null)
            {
                return _searchSurface;
            }

            var sb = new StringBuilder(Output.Length * 2);
            sb.Append(Output);

            switch (Destination)
            {
                case Destination.Html when Html.WellFormed:
                    sb.Append('\n').Append(Html.PlainText);
                    foreach (var text in Html.TextNodes)
                    {
                        sb.Append('\n').Append(text);
                    }

                    foreach (var element in Html.Elements)
                    {
                        foreach (var (_, value) in element.Attributes)
                        {
                            sb.Append('\n').Append(value);
                        }
                    }

                    break;

                case Destination.Json when Json is not null:
                    foreach (var value in JsonWalker.Strings(Json.RootElement))
                    {
                        sb.Append('\n').Append(value);
                    }

                    break;

                default:
                    if (Markup.ParseError is null)
                    {
                        sb.Append('\n').Append(Markup.PlainText);
                    }

                    break;
            }

            return _searchSurface = sb.ToString();
        }
    }

    public void Dispose() => _json?.Dispose();
}
