namespace Scribe.Core.Cleanup;

/// <summary>
/// Prompt text for AI cleanup. The fixed post-editor guardrails live in
/// <see cref="TextCleanupService"/>; this exposes the user-editable <b>writing style</b> portion so
/// the settings UI can show a sensible default and let the user tune the tone and formatting that
/// gets sent to the model on every dictation.
/// </summary>
public static class CleanupPrompt
{
    /// <summary>
    /// The default writing-style guidance shown in settings and used whenever the user has not
    /// supplied their own. Describes the punctuation, structure and tone Scribe applies when it
    /// polishes a transcript — phrased in the first person because it reads as the user's own
    /// instructions to the model.
    /// </summary>
    public const string DefaultWritingStyle =
        "Write in clear, natural, well-structured English. Use correct punctuation — commas, " +
        "periods, semicolons, colons, question marks, and parentheses — according to sentence " +
        "structure. Break long run-on speech into properly formed sentences, and start a new " +
        "paragraph when the topic shifts. Remove filler words and false starts (such as \"um\", " +
        "\"uh\", \"you know\", \"like\", and \"I mean\") and fix small grammar slips, while keeping " +
        "my meaning, intent, and vocabulary. Keep technical terms, product names, code, URLs, and " +
        "numbers exactly as spoken.";

    /// <summary>
    /// Returns the supplied writing style when it has content, otherwise the
    /// <see cref="DefaultWritingStyle"/>. Keeps prompt-building and the settings UI consistent.
    /// </summary>
    public static string ResolveWritingStyle(string? writingStyle) =>
        string.IsNullOrWhiteSpace(writingStyle) ? DefaultWritingStyle : writingStyle.Trim();
}
