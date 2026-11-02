using Microsoft.AI.Foundry.Local;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// Foundry Local's optional telemetry is off from the first configuration, the only one the SDK reads (the manager is a
/// process-wide singleton), and what its runtime still sends is disclosed from the SDK version it was read from.
/// </summary>
public sealed class FoundryLocalTelemetryTests
{
    [Fact]
    public async Task Every_configuration_the_service_hands_foundry_local_turns_non_essential_telemetry_off()
    {
        await using var harness = new CleanupHarness();
        Assert.True(harness.Service.CreateFoundryConfiguration().DisableNonessentialTelemetry);

        harness.Service.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.NotEmpty(harness.Host.Configurations);
        Assert.All(harness.Host.Configurations, configuration => Assert.True(configuration.DisableNonessentialTelemetry));
    }

    [Fact]
    public void The_startup_event_disclosure_was_read_from_the_foundry_local_sdk_in_use()
    {
        // CleanupDisclosure.FoundryLocalStartupEvent, PRIVACY.md and the README describe the ProcessInfo event Foundry Local
        // 2.1.0 still uploads with non-essential telemetry off (sdk_v2/cpp/src/telemetry/telemetry_metadata.cc and
        // one_ds_telemetry.cc at tag v2.1.0), including the 1DS SDK's network adapter identifier, which 2.1.0 replaces with
        // its own hashed one only while that telemetry is on. Another version can send something else.
        Assert.True(
            typeof(FoundryLocalManager).Assembly.GetName().Version == new Version(2, 1, 0, 0),
            "Foundry Local changed. Read ProcessInfo, the 1DS common context and the device identifier at the new tag, then " +
            "update CleanupDisclosure.FoundryLocalStartupEvent, PRIVACY.md and README.md, and this version.");
    }
}
