using Scribe.Core.Cleanup;

namespace Scribe.Evals;

/// <summary>Parsed command-line options for the eval runner.</summary>
internal sealed class CliOptions
{
    public CleanupProvider Provider { get; private set; } = CleanupProvider.FoundryLocal;
    public IReadOnlyList<string> Models { get; private set; } = [CleanupModelCatalog.DefaultAlias];
    public string? AzureEndpoint { get; private set; }
    public string? AzureTenantId { get; private set; }
    public TimeSpan ReadyTimeout { get; private set; } = TimeSpan.FromSeconds(240);
    public bool ListScenarios { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool Verbose { get; private set; }

    /// <summary>
    /// Builds the cleanup configuration for a single eval run: a model and a writing-style prompt on
    /// top of the selected provider. For Azure, the model name is the deployment; the Foundry alias
    /// slot keeps its default placeholder (unused by the Azure path).
    /// </summary>
    public CleanupOptions BuildOptions(string model, string writingStyle) => Provider switch
    {
        CleanupProvider.AzureFoundry => new CleanupOptions(
            Enabled: true,
            Provider: CleanupProvider.AzureFoundry,
            FoundryModelAlias: CleanupModelCatalog.DefaultAlias,
            AzureEndpoint: AzureEndpoint,
            AzureDeployment: model,
            AzureTenantId: AzureTenantId,
            WritingStyle: writingStyle),
        _ => new CleanupOptions(
            Enabled: true,
            Provider: CleanupProvider.FoundryLocal,
            FoundryModelAlias: model,
            AzureEndpoint: null,
            AzureDeployment: null,
            WritingStyle: writingStyle),
    };

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        var models = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : string.Empty;

            switch (arg.ToLowerInvariant())
            {
                case "-h" or "--help":
                    o.ShowHelp = true;
                    break;
                case "--list":
                    o.ListScenarios = true;
                    break;
                case "-v" or "--verbose":
                    o.Verbose = true;
                    break;
                case "--provider":
                    o.Provider = Next().ToLowerInvariant() is "azure" or "azurefoundry" or "cloud"
                        ? CleanupProvider.AzureFoundry
                        : CleanupProvider.FoundryLocal;
                    break;
                case "--model":
                    var m = Next();
                    if (!string.IsNullOrWhiteSpace(m)) models.Add(m.Trim());
                    break;
                case "--models":
                    models.AddRange(Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--endpoint":
                    o.AzureEndpoint = Next();
                    break;
                case "--tenant":
                    o.AzureTenantId = Next();
                    break;
                case "--ready-timeout":
                    if (int.TryParse(Next(), out var secs) && secs > 0)
                    {
                        o.ReadyTimeout = TimeSpan.FromSeconds(secs);
                    }
                    break;
            }
        }

        if (models.Count > 0)
        {
            o.Models = models;
        }

        return o;
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            Scribe.Evals — offline style/format eval harness for Scribe AI cleanup.

            Usage:
              dotnet run --project tools/Scribe.Evals -- [options]

            Options:
              --provider <foundrylocal|azure>   Cleanup backend (default: foundrylocal).
              --model <name>                    A model to test (Foundry alias, or Azure deployment).
                                                Repeatable.
              --models <a,b,c>                  Comma-separated models to compare head-to-head.
              --endpoint <url>                  Azure/Microsoft Foundry endpoint (azure provider).
              --tenant <id>                     Optional Azure tenant id override (azure provider).
              --ready-timeout <seconds>         Max wait for a model to load (default: 240).
              --list                            List the eval scenarios and exit.
              -v, --verbose                     Print service init/cleanup diagnostics to stderr.
              -h, --help                        Show this help.

            Examples:
              dotnet run --project tools/Scribe.Evals
              dotnet run --project tools/Scribe.Evals -- --models qwen3-1.7b,phi-3.5-mini
              dotnet run --project tools/Scribe.Evals -- --provider azure --endpoint https://x.openai.azure.com/ --model gpt-5.4-mini

            Exit code is 0 when every scenario follows its prompt, otherwise the number of failures.
            """);
    }
}
