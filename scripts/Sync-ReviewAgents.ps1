<#
.SYNOPSIS
    Synchronizes canonical Scribe review lenses in .claude/skills/scribe-code-review/agents/*.md
    to read-only GitHub Copilot custom agents in .github/agents/<lens>.agent.md (or verifies 1:1 sync with -Check).
#>
[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$SourceDir = Join-Path $RepoRoot ".claude\skills\scribe-code-review\agents"
$TargetDir = Join-Path $RepoRoot ".github\agents"
$MaxPromptChars = 30000

$AgentMetadata = @{
    "architecture-fit"          = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: P-1 to P-12 architecture fit and Core/App/Overlay boundaries." }
    "asr-pipeline"              = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: WASAPI capture, Silero VAD, Parakeet ASR chunking, and AsrCheck invariants." }
    "azure-credential"          = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: AzureCredentialFactory, CLI token cache/gate, Foundry role GUIDs, and OpenAI lockstep." }
    "build-packaging"           = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: win-x64/win-arm64 purity, central NuGet pins, WinUI Platform, Velopack, MSIX, and CI." }
    "comment-and-dash-hygiene"  = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: load-bearing why comments, narration noise, and the U+2014/U+2013 dash ban." }
    "core-app-layering"         = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: pure deciders in Scribe.Core with tests vs thin WPF/WinUI shell adapters." }
    "docs-sync"                 = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: PRIVACY.md, AGENTS.md, README.md, benchmarks, and macOS porting plan truth." }
    "finding-verification"      = @{ Model = "gpt-6.1-sol";      Description = "Scribe adversarial adjudicator: refutes or confirms drafted review findings against the diff and guards." }
    "fragile-area"              = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: historical regression surfaces (hooks, injection, overlay, cleanup, ASR, word packs)." }
    "guardrail-erosion"         = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: deleted/skipped tests, warning suppressions, weakened CI, and relaxed privacy pins." }
    "learned-patterns"          = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: applies active empirical rules from docs/derived-rules/ and nothing else." }
    "logging-discipline"        = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: non-throwing shared daily log, FailureShape privacy, TraceTagPolicy, and retention." }
    "macos-port"                = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: Swift 6 macOS port (ProcessRunner, ScribeLog, Keychain) and Scribe.Core parity." }
    "maintainer-decision"       = @{ Model = "claude-sonnet-5.5"; Description = "Scribe preflight adjudicator: identifies Ask-first boundaries and irreversible contract trade-offs." }
    "merit"                     = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: change rationale, runtime/log verification evidence, and Ask-first acknowledgement." }
    "overlay-process-contract"  = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: out-of-process WinUI pill, OverlayPipeProtocol, enum twins, and orphan safety." }
    "privacy-egress"            = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: fail-closed store=false, prompt cache, TryHandOff vocabulary admission, and zero audio egress." }
    "prompt-and-model"          = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: cleanup prompts, evals/benchmarks, Foundry Local EPs, and local context fitting." }
    "settings-and-persistence"  = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: AppSettings defaults/Clone/DPAPI, SQLite v7 additive schema, and AppPaths." }
    "solution-alternatives"     = @{ Model = "grok-4.7";         Description = "Scribe review lens: simpler cataloged/SDK shapes and root-cause problem fit." }
    "tests-coverage"            = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: untested new or altered behavior ranked by risk." }
    "tests-quality"             = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: whether tests can actually fail, honest seams, determinism, and pool/harness rules." }
    "tests-regression-pin"      = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: whether bug-fix tests construct the exact triggering state and fail on revert." }
    "ui-shell-quality"          = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: WPF-UI Fluent controls, AccentContrastPlanner, ScribeFont scaling, and accessibility." }
    "win32-interop"             = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: hook callback deadlines, SendInput short counts, ClipboardBorrower, and focus checks." }
    "word-packs-and-vocabulary" = @{ Model = "gemini-4-carbon";  Description = "Scribe review lens: LibraryPrecedence, built-in edits overlay, journaled saves, and VocabularyPublisher." }
}

$SourceFiles = @(Get-ChildItem -Path $SourceDir -Filter "*.md" | Sort-Object Name)
$Errors = New-Object System.Collections.Generic.List[string]

foreach ($File in $SourceFiles) {
    $Lens = [System.IO.Path]::GetFileNameWithoutExtension($File.Name)
    if (-not $AgentMetadata.ContainsKey($Lens)) {
        $Errors.Add("Missing metadata mapping in Sync-ReviewAgents.ps1 for lens '$Lens'.")
        continue
    }

    $Body = [System.IO.File]::ReadAllText($File.FullName).Replace("`r`n", "`n").TrimEnd("`n") + "`n"
    if ($Body.Length -ge $MaxPromptChars) {
        $Errors.Add("Lens '$Lens' body is $($Body.Length) chars, exceeding GitHub's $MaxPromptChars-char custom agent limit.")
    }
    if ($Body.IndexOf([char]0x2014) -ge 0 -or $Body.IndexOf([char]0x2013) -ge 0) {
        $Errors.Add("Lens '$Lens' contains a banned em dash (U+2014) or en dash (U+2013).")
    }

    $Meta = $AgentMetadata[$Lens]
    $Header = @(
        "---"
        "name: $Lens"
        "description: `"$($Meta.Description)`""
        "model: $($Meta.Model)"
        'tools: ["read", "search"]'
        "user-invocable: false"
        "---"
        ""
        ""
    ) -join "`n"

    $Expected = $Header + $Body
    $TargetPath = Join-Path $TargetDir "$Lens.agent.md"

    if ($Check) {
        if (-not (Test-Path -LiteralPath $TargetPath)) {
            $Errors.Add("Missing synced custom agent file: $TargetPath")
        } else {
            $Actual = [System.IO.File]::ReadAllText($TargetPath).Replace("`r`n", "`n")
            if (-not [string]::Equals($Expected, $Actual, [System.StringComparison]::Ordinal)) {
                $Errors.Add("Out-of-sync custom agent file: $TargetPath (run pwsh ./scripts/Sync-ReviewAgents.ps1).")
            }
        }
    } else {
        $Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($TargetPath, $Expected, $Utf8NoBom)
    }
}

foreach ($Key in $AgentMetadata.Keys) {
    $Candidate = Join-Path $SourceDir "$Key.md"
    if (-not (Test-Path -LiteralPath $Candidate)) {
        $Errors.Add("Metadata entry '$Key' has no matching lens file at $Candidate.")
    }
}

if ($Errors.Count -gt 0) {
    throw ($Errors -join [Environment]::NewLine)
}

if ($Check) {
    Write-Output "Verified $($SourceFiles.Count) Scribe review custom agents in sync."
} else {
    Write-Output "Synced $($SourceFiles.Count) Scribe review custom agents to $TargetDir."
}
