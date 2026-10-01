using Scribe.Core.Cleanup;

namespace Scribe.Core.Settings;

/// <summary>
/// The words Settings shows for the API another AI service is reached through (<see cref="CustomApiStyle"/>), and when it can
/// be chosen: only while the address names no API and is no app's own address (<see cref="CustomServiceAddress.Effective"/>).
/// The box's title, "API", is written in the window's XAML, where Find a setting reads it.
/// </summary>
public static class CustomApiStyleText
{
    /// <summary>What the box calls an API.</summary>
    public static string NameOf(CustomApiStyle style) =>
        style == CustomApiStyle.Responses ? "Responses" : "Chat Completions";

    /// <summary>The order the box lists them in.</summary>
    public static IReadOnlyList<CustomApiStyle> Choices { get; } = [CustomApiStyle.ChatCompletions, CustomApiStyle.Responses];

    /// <summary>What Scribe asks of a service reached through Responses, which OpenAI stores unless told not to.</summary>
    public const string ResponsesStoreNotice = "With Responses, Scribe asks the service not to store responses.";

    /// <summary>True when the API can be chosen for <paramref name="address"/>: it names none, and is no app's own address.</summary>
    public static bool CanChoose(string? address) =>
        CustomServiceAddress.NamedStyle(address) is null && LocalAiServer.AppAt(address?.Trim()) == LocalServerApp.None;

    /// <summary>The line under the box, for what the address says.</summary>
    public static string Hint(string? address)
    {
        var app = LocalAiServer.AppAt(address?.Trim());
        if (app != LocalServerApp.None)
        {
            return $"{(app == LocalServerApp.Ollama ? "Ollama" : "LM Studio")} at its own address uses Chat Completions.";
        }

        return CustomServiceAddress.NamedStyle(address) switch
        {
            CustomApiStyle.Responses => "This address ends in /responses, so Scribe uses Responses. " + ResponsesStoreNotice,
            CustomApiStyle.ChatCompletions => "This address ends in /chat/completions, so Scribe uses Chat Completions.",
            _ => "Most services take Chat Completions. Choose Responses only if your service needs it. " + ResponsesStoreNotice,
        };
    }

    /// <summary>The line under the server address box.</summary>
    public const string AddressHint =
        "The service's OpenAI-compatible address, usually ending in /v1. An address that ends in /chat/completions or " +
        "/responses is used as it is. For Ollama or LM Studio on this PC, choose On this PC instead.";
}
