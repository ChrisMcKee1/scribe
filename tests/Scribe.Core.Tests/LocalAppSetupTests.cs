using Scribe.Core.Cleanup;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>What Settings shows for Ollama and LM Studio under "On this PC".</summary>
public sealed class LocalAppSetupTests
{
    private static LocalServerModel Model(string id, string? name = null) => new(id, name ?? id, 1_000_000_000);

    [Fact]
    public void The_chosen_model_stays_chosen_spelled_as_it_was_saved()
    {
        LocalServerModel[] models = [Model("gemma4:e2b"), Model("llama3.2:3b")];

        var (listed, selected) = LocalAppSetup.ModelChoices(models, "llama3.2:3b");
        Assert.Equal("llama3.2:3b", selected);
        Assert.Equal(["gemma4:e2b", "llama3.2:3b"], listed.Select(model => model.Id));

        // The same model in another spelling keeps the saved one, so opening Settings and saving changes nothing.
        (listed, selected) = LocalAppSetup.ModelChoices([Model("llama3.2:latest"), Model("gemma4:e2b")], " llama3.2 ");
        Assert.Equal("llama3.2", selected);
        Assert.Equal(["llama3.2", "gemma4:e2b"], listed.Select(model => model.Id));
        Assert.Equal("llama3.2", listed[0].DisplayName);

        // LM Studio's own name for the model stays in the list's label.
        (listed, selected) = LocalAppSetup.ModelChoices([Model("google/gemma-4-e2b", "Gemma 4 E2B")], "Google/Gemma-4-E2B");
        Assert.Equal("Google/Gemma-4-E2B", selected);
        Assert.Equal("Gemma 4 E2B", listed.Single().DisplayName);
    }

    [Fact]
    public void A_chosen_model_the_app_does_not_list_is_kept_rather_than_swapped()
    {
        // An LM Studio instance loaded under a name of its own, or a model the app no longer has: never replaced silently.
        var (listed, selected) = LocalAppSetup.ModelChoices([Model("google/gemma-4-e2b")], "my-cleanup-model", roomyGraphicsCard: true);

        Assert.Equal("my-cleanup-model", selected);
        Assert.Equal(["my-cleanup-model", "google/gemma-4-e2b"], listed.Select(model => model.Id));

        // Before the app answers, the list is the chosen model alone, or nothing.
        Assert.Equal(("gemma4:e2b", 1), Shape(LocalAppSetup.ModelChoices([], "gemma4:e2b")));
        Assert.Equal((null, 0), Shape(LocalAppSetup.ModelChoices([], null)));

        static (string? Selected, int Count) Shape((IReadOnlyList<LocalServerModel> Models, string? Selected) choices) =>
            (choices.Selected, choices.Models.Count);
    }

    [Fact]
    public void With_nothing_chosen_the_benchmark_s_order_picks_in_either_app_s_spelling()
    {
        Assert.Equal("gemma4:e2b", LocalAppSetup.PickModel([Model("llama3.2:3b"), Model("gemma4:e4b"), Model("gemma4:e2b")]));
        Assert.Equal("gemma4:e4b", LocalAppSetup.PickModel([Model("llama3.2:3b"), Model("gemma4:e4b")]));
        Assert.Equal(
            "qwen/qwen3-4b-2507",
            LocalAppSetup.PickModel([Model("ibm/granite-4-micro"), Model("qwen/qwen3-4b-2507")]));
        Assert.Equal("google/gemma-4-e2b", LocalAppSetup.PickModel([Model("ibm/granite-4-micro"), Model("google/gemma-4-e2b")]));
        Assert.Equal("gemma4:e2b", LocalAppSetup.ModelChoices([Model("llama3.2:3b"), Model("gemma4:e2b")], null).Selected);
    }

    [Fact]
    public void A_graphics_card_with_room_for_it_puts_the_larger_Gemma_first()
    {
        LocalServerModel[] models = [Model("gemma4:e2b"), Model("gemma4:e4b"), Model("qwen3:4b-instruct")];

        Assert.Equal("gemma4:e4b", LocalAppSetup.PickModel(models, roomyGraphicsCard: true));
        Assert.Equal("gemma4:e2b", LocalAppSetup.PickModel(models, roomyGraphicsCard: false));
        Assert.Equal("gemma4:e2b", LocalAppSetup.ModelChoices(models, "gemma4:e2b", roomyGraphicsCard: true).Selected);
        Assert.Equal("qwen3:4b-instruct", LocalAppSetup.PickModel([Model("qwen3:4b-instruct")], roomyGraphicsCard: true));
    }

    [Fact]
    public void A_model_outside_the_benchmark_is_still_picked_and_an_empty_list_picks_nothing()
    {
        Assert.Equal("mystery:7b", LocalAppSetup.PickModel([Model("mystery:7b"), Model("other:1b")]));
        Assert.Null(LocalAppSetup.PickModel([]));
    }

    [Fact]
    public void The_status_line_says_what_to_do_when_the_app_is_not_there_or_has_no_models()
    {
        var checking = LocalAppSetup.Describe(LocalServerApp.Ollama, state: null, "gemma4:e2b", 10);
        Assert.Equal(AiCleanupStatusKind.Busy, checking.Kind);
        Assert.Null(checking.Primary);

        var closed = LocalAppSetup.Describe(LocalServerApp.LmStudio, LocalServerState.NotRunning, "gemma4:e2b", 10);
        Assert.Equal("Scribe can't reach LM Studio. Open LM Studio, then choose Check again.", closed.Text);
        Assert.Equal(AiCleanupActionId.CheckAgain, closed.Primary!.Id);

        var failed = LocalAppSetup.Describe(LocalServerApp.Ollama, LocalServerState.Failed, "gemma4:e2b", 10);
        Assert.Equal(AiCleanupActionId.CheckAgain, failed.Primary!.Id);

        var empty = LocalAppSetup.Describe(LocalServerApp.Ollama, new LocalServerState(LocalServerReach.Reached, [], []), null, 10);
        Assert.Equal("Ollama has no models yet. Download one in Ollama, then choose Check again.", empty.Text);

        var needsKey = LocalAppSetup.Describe(LocalServerApp.LmStudio, LocalServerState.NeedsKey, "google/gemma-4-e2b", 10);
        Assert.Equal(AiCleanupStatusKind.Warning, needsKey.Kind);
        Assert.Equal("LM Studio asks for an API key. To use one, choose Another AI service and enter the key there.", needsKey.Text);
        Assert.Equal(AiCleanupActionId.CheckAgain, needsKey.Primary!.Id);
    }

    [Fact]
    public void A_chosen_model_the_app_does_not_list_says_so_unless_the_app_holds_it()
    {
        var state = new LocalServerState(
            LocalServerReach.Reached,
            [Model("google/gemma-4-e2b")],
            [new LocalServerLoadedModel("google/gemma-4-e2b", 3_000_000_000), new LocalServerLoadedModel("my-cleanup-model", 3_000_000_000)]);

        var missing = LocalAppSetup.Describe(LocalServerApp.LmStudio, state, "gone:1b", 10);
        Assert.Equal(AiCleanupStatusKind.Warning, missing.Kind);
        Assert.Equal("LM Studio doesn't list gone:1b. Choose another model, or download it in LM Studio.", missing.Text);
        Assert.Equal(AiCleanupActionId.CheckAgain, missing.Primary!.Id);

        // An instance loaded under a name of its own is in memory under that name, so Free memory applies to it.
        var named = LocalAppSetup.Describe(LocalServerApp.LmStudio, state, "my-cleanup-model", 10);
        Assert.Equal(AiCleanupStatusKind.Success, named.Kind);
        Assert.Equal(AiCleanupActionId.Unload, named.Primary!.Id);

        // Ollama's :latest is the same model, listed.
        var ollama = new LocalServerState(LocalServerReach.Reached, [Model("llama3.2:latest")], []);
        Assert.Equal(AiCleanupStatusKind.Info, LocalAppSetup.Describe(LocalServerApp.Ollama, ollama, "llama3.2", 10).Kind);
    }

    [Fact]
    public void A_loaded_model_shows_its_memory_when_Scribe_frees_it_and_Free_memory() => InvariantCulture(() =>
    {
        var state = new LocalServerState(
            LocalServerReach.Reached, [Model("gemma4:e2b")], [new LocalServerLoadedModel("gemma4:e2b", 1_706_000_000)]);

        var row = LocalAppSetup.Describe(LocalServerApp.Ollama, state, "gemma4:e2b", 10);
        Assert.Equal(AiCleanupStatusKind.Success, row.Kind);
        Assert.Equal("gemma4:e2b is using 1.6 GB of memory. Scribe frees it after 10 minutes without a dictation.", row.Text);
        Assert.Equal(AiCleanupActionId.Unload, row.Primary!.Id);
        Assert.Equal(FoundryLocalSetup.FreeMemoryAction, row.Primary.Text);

        // "Never free memory" promises no time.
        Assert.Equal("gemma4:e2b is using 1.6 GB of memory.", LocalAppSetup.Describe(LocalServerApp.Ollama, state, "gemma4:e2b", 0).Text);
        Assert.EndsWith("after 1 minute without a dictation.", LocalAppSetup.Describe(LocalServerApp.Ollama, state, "gemma4:e2b", 1).Text, StringComparison.Ordinal);
    });

    [Fact]
    public void A_model_not_in_memory_loads_when_the_user_dictates()
    {
        var state = new LocalServerState(LocalServerReach.Reached, [Model("gemma4:e2b")], []);

        var row = LocalAppSetup.Describe(LocalServerApp.Ollama, state, "gemma4:e2b", 10);

        Assert.Equal(AiCleanupStatusKind.Info, row.Kind);
        Assert.Equal("gemma4:e2b isn't using memory now. It loads when you dictate.", row.Text);
        Assert.Null(row.Primary);
    }

    [Theory]
    [InlineData(1_706_000_000L, "1.6 GB")]
    [InlineData(1_073_741_824L, "1.0 GB")]
    [InlineData(891_289_600L, "850 MB")]
    [InlineData(10L, "1 MB")]
    public void Sizes_read_as_Task_Manager_counts_them(long bytes, string expected) =>
        InvariantCulture(() => Assert.Equal(expected, LocalAppSetup.FormatSize(bytes)));

    private static void InvariantCulture(Action test)
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            test();
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }
}
