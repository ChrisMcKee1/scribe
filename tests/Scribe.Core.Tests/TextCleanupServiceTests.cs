using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// The AI-cleanup toggle must apply live. The tray quick-toggle and the settings window both reach the
/// engine through <see cref="TextCleanupService.Configure"/> (via DictationController.ApplySettings), so
/// a check/uncheck has to change behavior on the very next dictation with no app restart. These tests
/// pin the engine's half of that contract: <see cref="TextCleanupService.Configure"/> reacts on the
/// calling thread and <see cref="TextCleanupService.CleanAsync"/> honors the new state immediately.
/// </summary>
public sealed class TextCleanupServiceTests
{
    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("https://example.com/v1", true)]
    [InlineData("http://example.com/v1", false)]
    public void Custom_endpoint_requires_https_except_for_loopback(string value, bool expected)
    {
        Assert.Equal(expected, TextCleanupService.TryValidateCustomEndpoint(value, out _, out _));
    }

    [Fact]
    public async Task Unchecking_cleanup_disables_it_immediately_without_restart()
    {
        await using var svc = new TextCleanupService(NullLogger<TextCleanupService>.Instance);

        // "Uncheck" the box. Configure with a disabled snapshot flips the status synchronously — there
        // is no background work and no relaunch — so the next dictation passes raw text straight through.
        svc.Configure(CleanupOptions.Disabled);

        Assert.Equal(CleanupStatus.Disabled, svc.Status);

        var result = await svc.CleanAsync("can you hear me now");

        Assert.Equal(CleanupOutcome.Skipped, result.Outcome);
        Assert.Equal("can you hear me now", result.Text);
    }

    [Fact]
    public async Task Toggling_cleanup_on_then_off_takes_effect_live()
    {
        await using var svc = new TextCleanupService(NullLogger<TextCleanupService>.Instance);

        // "Check" the box. An Azure provider with no endpoint configured yet is enabled but not
        // actionable, so the engine reacts synchronously (leaves Disabled for Unavailable) without
        // spawning a real model or a network call. The point is that it reacts at all — immediately,
        // rather than waiting for a relaunch.
        svc.Configure(CleanupOptions.Disabled with { Enabled = true, Provider = CleanupProvider.AzureFoundry });
        Assert.NotEqual(CleanupStatus.Disabled, svc.Status);

        // Not Ready, so dictation is never blocked: it passes through untouched while enabled-but-not-ready.
        var whileEnabling = await svc.CleanAsync("please book the demo room for thursday");
        Assert.Equal(CleanupOutcome.Skipped, whileEnabling.Outcome);

        // "Uncheck" the box again: back to Disabled synchronously, still passing raw text through.
        svc.Configure(CleanupOptions.Disabled);
        Assert.Equal(CleanupStatus.Disabled, svc.Status);

        var afterDisable = await svc.CleanAsync("please book the demo room for thursday");
        Assert.Equal(CleanupOutcome.Skipped, afterDisable.Outcome);
        Assert.Equal("please book the demo room for thursday", afterDisable.Text);
    }
}
