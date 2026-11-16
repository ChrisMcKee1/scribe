namespace Scribe.Core.Tests;

/// <summary>
/// Pins the Copilot App and Copilot CLI contract for <c>scribe-code-review</c>: ordered model roster,
/// 1:1 synchronization between <c>.claude/skills/scribe-code-review/agents/*.md</c> and
/// <c>.github/agents/&lt;lens&gt;.agent.md</c>, GitHub's 30,000-character custom-agent prompt limit,
/// read-only sub-agent tool restrictions, and Scribe's repository-wide dash ban.
/// </summary>
public sealed class ScribeCodeReviewSkillTests
{
    private const int MaxCustomAgentPromptChars = 30_000;

    private static readonly IReadOnlyDictionary<string, (string Model, string Description)> ExpectedAgents =
        new Dictionary<string, (string Model, string Description)>(StringComparer.Ordinal)
        {
            ["architecture-fit"] = ("gemini-4-carbon", "Scribe review lens: P-1 to P-12 architecture fit and Core/App/Overlay boundaries."),
            ["asr-pipeline"] = ("gemini-4-carbon", "Scribe review lens: WASAPI capture, Silero VAD, Parakeet ASR chunking, and AsrCheck invariants."),
            ["azure-credential"] = ("gemini-4-carbon", "Scribe review lens: AzureCredentialFactory, CLI token cache/gate, Foundry role GUIDs, and OpenAI lockstep."),
            ["build-packaging"] = ("gemini-4-carbon", "Scribe review lens: win-x64/win-arm64 purity, central NuGet pins, WinUI Platform, Velopack, MSIX, and CI."),
            ["comment-and-dash-hygiene"] = ("gemini-4-carbon", "Scribe review lens: load-bearing why comments, narration noise, and the U+2014/U+2013 dash ban."),
            ["core-app-layering"] = ("gemini-4-carbon", "Scribe review lens: pure deciders in Scribe.Core with tests vs thin WPF/WinUI shell adapters."),
            ["docs-sync"] = ("gemini-4-carbon", "Scribe review lens: PRIVACY.md, AGENTS.md, README.md, benchmarks, and macOS porting plan truth."),
            ["finding-verification"] = ("gpt-6.1-sol", "Scribe adversarial adjudicator: refutes or confirms drafted review findings against the diff and guards."),
            ["fragile-area"] = ("gemini-4-carbon", "Scribe review lens: historical regression surfaces (hooks, injection, overlay, cleanup, ASR, word packs)."),
            ["guardrail-erosion"] = ("gemini-4-carbon", "Scribe review lens: deleted/skipped tests, warning suppressions, weakened CI, and relaxed privacy pins."),
            ["learned-patterns"] = ("gemini-4-carbon", "Scribe review lens: applies active empirical rules from docs/derived-rules/ and nothing else."),
            ["logging-discipline"] = ("gemini-4-carbon", "Scribe review lens: non-throwing shared daily log, FailureShape privacy, TraceTagPolicy, and retention."),
            ["macos-port"] = ("gemini-4-carbon", "Scribe review lens: Swift 6 macOS port (ProcessRunner, ScribeLog, Keychain) and Scribe.Core parity."),
            ["maintainer-decision"] = ("claude-sonnet-5.5", "Scribe preflight adjudicator: identifies Ask-first boundaries and irreversible contract trade-offs."),
            ["merit"] = ("gemini-4-carbon", "Scribe review lens: change rationale, runtime/log verification evidence, and Ask-first acknowledgement."),
            ["overlay-process-contract"] = ("gemini-4-carbon", "Scribe review lens: out-of-process WinUI pill, OverlayPipeProtocol, enum twins, and orphan safety."),
            ["privacy-egress"] = ("gemini-4-carbon", "Scribe review lens: fail-closed store=false, prompt cache, TryHandOff vocabulary admission, and zero audio egress."),
            ["prompt-and-model"] = ("gemini-4-carbon", "Scribe review lens: cleanup prompts, evals/benchmarks, Foundry Local EPs, and local context fitting."),
            ["settings-and-persistence"] = ("gemini-4-carbon", "Scribe review lens: AppSettings defaults/Clone/DPAPI, SQLite v7 additive schema, and AppPaths."),
            ["solution-alternatives"] = ("grok-4.7", "Scribe review lens: simpler cataloged/SDK shapes and root-cause problem fit."),
            ["tests-coverage"] = ("gemini-4-carbon", "Scribe review lens: untested new or altered behavior ranked by risk."),
            ["tests-quality"] = ("gemini-4-carbon", "Scribe review lens: whether tests can actually fail, honest seams, determinism, and pool/harness rules."),
            ["tests-regression-pin"] = ("gemini-4-carbon", "Scribe review lens: whether bug-fix tests construct the exact triggering state and fail on revert."),
            ["ui-shell-quality"] = ("gemini-4-carbon", "Scribe review lens: WPF-UI Fluent controls, AccentContrastPlanner, ScribeFont scaling, and accessibility."),
            ["win32-interop"] = ("gemini-4-carbon", "Scribe review lens: hook callback deadlines, SendInput short counts, ClipboardBorrower, and focus checks."),
            ["word-packs-and-vocabulary"] = ("gemini-4-carbon", "Scribe review lens: LibraryPrecedence, built-in edits overlay, journaled saves, and VocabularyPublisher."),
        };

    private static readonly HashSet<string> NonFindingNormalOrAdjudicatorLenses =
    [
        "finding-verification",
        "maintainer-decision",
        "solution-alternatives",
    ];

    [Fact]
    public void Skill_manifest_declares_copilot_roster_long_context_and_workspace_cache()
    {
        var skillPath = Path.Combine(RepositoryRoot(), ".claude", "skills", "scribe-code-review", "SKILL.md");
        var content = NormalizeNewlines(File.ReadAllText(skillPath));
        var lines = content.Split('\n');

        Assert.True(lines.Length <= 500, $"SKILL.md is {lines.Length} lines; keep it at or below 500 lines.");
        Assert.StartsWith("---\nname: scribe-code-review\ndescription:", content, StringComparison.Ordinal);

        var geminiIndex = content.IndexOf("`gemini-4-carbon`", StringComparison.Ordinal);
        var gptIndex = content.IndexOf("`gpt-6.1-sol`", StringComparison.Ordinal);
        var grokIndex = content.IndexOf("`grok-4.7`", StringComparison.Ordinal);
        var sonnetIndex = content.IndexOf("`claude-sonnet-5.5`", StringComparison.Ordinal);

        Assert.True(geminiIndex >= 0, "SKILL.md must declare Rank 1 model gemini-4-carbon.");
        Assert.True(gptIndex > geminiIndex, "SKILL.md must declare Rank 2 model gpt-6.1-sol after gemini-4-carbon.");
        Assert.True(grokIndex > gptIndex, "SKILL.md must declare Rank 3 model grok-4.7 after gpt-6.1-sol.");
        Assert.True(sonnetIndex > grokIndex, "SKILL.md must declare Rank 4 model claude-sonnet-5.5 after grok-4.7.");

        Assert.Contains("long_context", content, StringComparison.Ordinal);
        Assert.Contains("reasoning_effort", content, StringComparison.Ordinal);
        Assert.Contains("artifacts/scribe-code-review/", content, StringComparison.Ordinal);
        Assert.DoesNotContain("claude-opus-5", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gpt-6-astra", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_review_lens_is_synced_to_a_valid_readonly_github_custom_agent()
    {
        var root = RepositoryRoot();
        var sourceDir = Path.Combine(root, ".claude", "skills", "scribe-code-review", "agents");
        var targetDir = Path.Combine(root, ".github", "agents");
        var skillText = NormalizeNewlines(File.ReadAllText(
            Path.Combine(root, ".claude", "skills", "scribe-code-review", "SKILL.md")));

        var step4Start = skillText.IndexOf("## Step 4: Synthesize", StringComparison.Ordinal);
        var step45Start = skillText.IndexOf("## Step 4.5:", StringComparison.Ordinal);
        Assert.True(step4Start >= 0 && step45Start > step4Start, "SKILL.md must contain Step 4 and Step 4.5 sections.");
        var step4Section = skillText[step4Start..step45Start];

        var actualLenses = Directory.GetFiles(sourceDir, "*.md")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedAgents.Keys.Order(StringComparer.Ordinal), actualLenses);

        foreach (var (lens, meta) in ExpectedAgents)
        {
            var sourcePath = Path.Combine(sourceDir, $"{lens}.md");
            var targetPath = Path.Combine(targetDir, $"{lens}.agent.md");
            Assert.True(File.Exists(targetPath), $"Missing synced custom agent file: {targetPath}");

            var body = NormalizeNewlines(File.ReadAllText(sourcePath)).TrimEnd('\n') + "\n";
            Assert.True(
                body.Length < MaxCustomAgentPromptChars,
                $"Lens '{lens}' is {body.Length} chars, exceeding GitHub's {MaxCustomAgentPromptChars}-char custom-agent limit.");
            Assert.Contains($"[[agent-done:{lens} findings=<n> coverage=complete]]", body, StringComparison.Ordinal);

            var expectedHeader = string.Join(
                "\n",
                "---",
                $"name: {lens}",
                $"description: \"{meta.Description}\"",
                $"model: {meta.Model}",
                "tools: [\"read\", \"search\"]",
                "user-invocable: false",
                "---",
                string.Empty,
                string.Empty);

            var expectedAgentFile = expectedHeader + body;
            var actualAgentFile = NormalizeNewlines(File.ReadAllText(targetPath));

            Assert.DoesNotContain("disable-model-invocation", expectedHeader, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\ninfer:", expectedHeader, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(expectedAgentFile, actualAgentFile);

            Assert.Contains($"`{lens}`", skillText, StringComparison.Ordinal);
            if (!NonFindingNormalOrAdjudicatorLenses.Contains(lens))
            {
                Assert.Contains($"`{lens}`", step4Section, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Skill_agents_and_review_scripts_contain_no_em_or_en_dashes()
    {
        var root = RepositoryRoot();
        var filesToScan = new List<string>();

        filesToScan.AddRange(Directory.GetFiles(
            Path.Combine(root, ".claude", "skills", "scribe-code-review"),
            "*.md",
            SearchOption.AllDirectories));

        foreach (var lens in ExpectedAgents.Keys)
        {
            filesToScan.Add(Path.Combine(root, ".github", "agents", $"{lens}.agent.md"));
        }

        filesToScan.Add(Path.Combine(root, "scripts", "New-ReviewCache.ps1"));
        filesToScan.Add(Path.Combine(root, "scripts", "Sync-ReviewAgents.ps1"));
        filesToScan.Add(Path.Combine(root, "tests", "Scribe.Core.Tests", "ScribeCodeReviewSkillTests.cs"));

        foreach (var file in filesToScan.Order(StringComparer.Ordinal))
        {
            Assert.True(File.Exists(file), $"Expected file missing: {file}");
            var text = File.ReadAllText(file);
            Assert.True(
                text.IndexOf('\u2014') < 0 && text.IndexOf('\u2013') < 0,
                $"Banned em dash (U+2014) or en dash (U+2013) found in {Path.GetRelativePath(root, file)}.");
        }
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
