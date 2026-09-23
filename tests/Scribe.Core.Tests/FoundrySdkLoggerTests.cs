using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;
using Scribe.Core.Tests.CleanupLogging;

namespace Scribe.Core.Tests;

/// <summary>
/// The Foundry Local SDK logs its own failures, raw exception text included, through the logger
/// Scribe gives it. That logger folds the user profile away and keeps everything else, because the
/// SDK's download hosts and loopback port are not user data and are what makes its failures
/// diagnosable.
/// </summary>
public sealed class FoundrySdkLoggerTests
{
    private const string Profile = @"C:\Users\scribe-canary-user";
    private const string UserName = "scribe-canary-user";

    [Fact]
    public void The_profile_is_folded_out_of_the_message_the_state_and_the_exception_text()
    {
        var inner = new CapturingLogger<TextCleanupService>();
        var sdk = new FoundrySdkLogger(inner, Profile);

        // The shapes FoundryLocalException produces: a message quoting native JSON (escaped
        // backslashes) and an inner exception quoting a path, logged through LogError(ex, message).
        var failure = new InvalidOperationException(
            @"Error downloading model qwen3-1.7b: could not write C:\Users\SCRIBE-CANARY-USER\.Scribe\cache\models\m.onnx",
            new IOException("Access denied: file:///C:/Users/scribe-canary-user/.Scribe/ep/cuda-ep/cuda.dll"));
        sdk.LogError(
            failure,
            "Download of {Model} failed: {Detail} from https://foundrylocal.azure-api.net/ via http://127.0.0.1:59317",
            "qwen3-1.7b",
            @"{""path"":""C:\\Users\\scribe-canary-user\\.Scribe\\cache""}");

        var entry = Assert.Single(inner.Entries);
        Assert.DoesNotContain(UserName, entry.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(entry.Exception);
        Assert.Empty(entry.State);

        Assert.Contains(@"%USERPROFILE%\.Scribe\cache\models\m.onnx", entry.Message);
        Assert.Contains("file:///%USERPROFILE%/.Scribe/ep/cuda-ep/cuda.dll", entry.Message);
        Assert.Contains(@"%USERPROFILE%\\.Scribe\\cache", entry.Message);

        // Everything that is not the profile passes through, so the SDK's diagnosis survives.
        Assert.Contains("https://foundrylocal.azure-api.net/", entry.Message);
        Assert.Contains("127.0.0.1:59317", entry.Message);
        Assert.Contains("qwen3-1.7b", entry.Message);
        Assert.Contains("System.InvalidOperationException", entry.Message);
        Assert.Contains("System.IO.IOException", entry.Message);
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Theory]
    [InlineData(@"C:\Users\chris\.Scribe", @"%USERPROFILE%\.Scribe")]
    [InlineData(@"C:\Users\chris", "%USERPROFILE%")]
    [InlineData(@"at C:\Users\CHRIS.", "at %USERPROFILE%.")]
    [InlineData(@"'C:\Users\chris'", "'%USERPROFILE%'")]
    [InlineData(@"C:\Users\christine\.Scribe", @"C:\Users\christine\.Scribe")]
    [InlineData(@"C:\Users\chris-old\x", @"C:\Users\chris-old\x")]
    [InlineData(@"D:\Users\chris\x", @"D:\Users\chris\x")]
    public void Only_this_profile_is_folded_and_only_at_a_path_boundary(string text, string expected)
    {
        Assert.Equal(expected, FoundrySdkLogger.RedactProfile(text, FoundrySdkLogger.SpellingsOf(@"C:\Users\chris")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\")]
    [InlineData("relative")]
    public void A_profile_that_is_not_a_real_directory_folds_nothing(string? profile)
    {
        Assert.Empty(FoundrySdkLogger.SpellingsOf(profile));
        Assert.Equal(@"C:\Users\chris\x", FoundrySdkLogger.RedactProfile(@"C:\Users\chris\x", FoundrySdkLogger.SpellingsOf(profile)));
    }

    [Fact]
    public void A_failure_inside_logging_never_reaches_the_sdk()
    {
        var inner = new CapturingLogger<TextCleanupService>();
        var sdk = new FoundrySdkLogger(inner, Profile);

        sdk.Log<object?>(LogLevel.Warning, 0, null, null, (_, _) => throw new FormatException("bad template"));
        sdk.LogWarning(new ToStringThrows(), "Probe failed.");

        var entry = Assert.Single(inner.Entries);
        Assert.Contains("Probe failed.", entry.Message);
        Assert.Contains(nameof(ToStringThrows), entry.Message);
    }

    [Fact]
    public async Task The_service_hands_the_sdk_the_folding_logger()
    {
        await using var harness = new CleanupHarness();
        Assert.NotEmpty(await harness.Service.ListFoundryModelsAsync());
        var sdk = Assert.IsType<FoundrySdkLogger>(harness.Host.SdkLogger);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        sdk.LogError(new IOException($"Could not open {home}\\.Scribe\\ep\\cuda-ep\\cuda.dll"), "EP registration failed.");

        var entry = Assert.Single(harness.Log.Entries, e => e.Message.StartsWith("EP registration failed."));
        Assert.DoesNotContain(home, entry.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"%USERPROFILE%\.Scribe\ep\cuda-ep\cuda.dll", entry.Message);
    }

    private sealed class ToStringThrows : Exception
    {
        public override string ToString() => throw new InvalidOperationException("no text");
    }
}
