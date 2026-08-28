namespace Scribe.Core.Feedback;

using System.Text;
using Scribe.Core.Models;

/// <summary>
/// Builds the report a user sends when an AI result is inappropriate, and the <c>mailto:</c> that
/// carries it. Pure: it composes text and a URI and sends nothing itself.
/// </summary>
/// <remarks>
/// <para>
/// This exists because Microsoft Store policy 11.16 requires that a product generating live AI
/// content "provide a means for users to report inappropriate content to the developer". Scribe's
/// 0.3.12 submission was rejected for its absence.
/// </para>
/// <para>
/// The obvious implementation posts the content to an endpoint. Scribe cannot do that. PRIVACY.md
/// states that nothing is transmitted, "fully offline" is the product's central claim, and a report
/// about a bad rewrite necessarily contains the user's own words. So the app composes the report and
/// hands it to the user's mail client; the user reads it and decides whether to send. Nothing leaves
/// the device unless a person chooses to send it, which satisfies the policy without making the
/// privacy statement false.
/// </para>
/// <para>
/// The same reasoning rules out the Store's own channels. Feedback Hub posts are public and
/// upvotable, the Partner Center Feedback report was deprecated in April 2020, and Store reviews are
/// public. Every one of them would publish the user's private dictation to satisfy a policy about
/// protecting users.
/// </para>
/// </remarks>
public static class AiContentReport
{
    /// <summary>Where reports go. Not a GitHub URL: Store certification rejected that explicitly.</summary>
    public const string SupportAddress = "support@mckeesolutions.ai";

    /// <summary>
    /// Composes the report body.
    /// </summary>
    /// <param name="output">The AI-generated text being reported. The whole point of the report.</param>
    /// <param name="provider">Which backend produced it, e.g. AzureFoundry or FoundryLocal.</param>
    /// <param name="model">The model id, so a bad prompt can be traced to what ran it.</param>
    /// <param name="appVersion">Scribe's version, because prompts change between releases.</param>
    /// <param name="whenUtc">When the result was produced.</param>
    /// <param name="sourceText">
    /// What the user originally said. Optional and excluded by default: it is the most sensitive
    /// thing in the report and a reviewer can usually judge the output without it. Including it is
    /// the user's decision, made in the dialog, not a default the app chose for them.
    /// </param>
    public static string Build(
        string output,
        string provider,
        string model,
        string appVersion,
        DateTimeOffset whenUtc,
        string? sourceText = null)
    {
        var builder = new StringBuilder(1024);

        builder.AppendLine("Reporting an AI result produced by Scribe.")
               .AppendLine()
               .AppendLine($"Scribe version : {appVersion}")
               .AppendLine($"Provider       : {provider}")
               .AppendLine($"Model          : {model}")
               .AppendLine($"Produced (UTC) : {whenUtc:yyyy-MM-dd HH:mm:ss}")
               .AppendLine()
               .AppendLine("--- AI OUTPUT ---")
               .AppendLine(output ?? string.Empty)
               .AppendLine("--- END AI OUTPUT ---");

        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            builder.AppendLine()
                   .AppendLine("--- SOURCE TEXT (included by the reporter) ---")
                   .AppendLine(sourceText)
                   .AppendLine("--- END SOURCE TEXT ---");
        }

        builder.AppendLine()
               .AppendLine("What was wrong with this result:")
               .AppendLine()
               .AppendLine("[describe the problem here]");

        return builder.ToString();
    }

    /// <summary>
    /// Wraps a report in a <c>mailto:</c> URI.
    /// </summary>
    /// <remarks>
    /// Percent-encoding is done with <see cref="Uri.EscapeDataString"/> rather than a query-string
    /// helper, because a mail client parses this as a URI and an unescaped newline or ampersand in
    /// the body silently truncates everything after it. That failure would be invisible: the user
    /// sees a composed message and never learns that the half naming the problem was dropped.
    /// </remarks>
    public static string BuildMailtoUri(string subject, string body) =>
        $"mailto:{SupportAddress}" +
        $"?subject={Uri.EscapeDataString(subject ?? string.Empty)}" +
        $"&body={Uri.EscapeDataString(body ?? string.Empty)}";

    /// <summary>The subject line, carrying the version so triage can sort without opening anything.</summary>
    public static string BuildSubject(string appVersion) =>
        $"Scribe {appVersion}: report an AI result";

    /// <summary>
    /// True when a result can be reported at all. A deterministic transformation, such as applying
    /// the user's own dictionary, is not generative AI content and reporting it to a developer as
    /// inappropriate AI output would be meaningless.
    /// </summary>
    public static bool CanReport(AiRating rating, bool producedByAi) => producedByAi;
}
