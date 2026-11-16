<#
.SYNOPSIS
    Builds an isolated, workspace-local review cache under artifacts/scribe-code-review/<run-id>/
    so GitHub Copilot App and Copilot CLI review sub-agents can read diffs and metadata via view.
#>
[CmdletBinding()]
param(
    [ValidateSet("WorkingTree", "Branch", "PullRequest")]
    [string]$Target = "WorkingTree",

    [int]$PullRequest = 0,

    [string]$BaseRef = "main",

    [string]$HeadRef = "HEAD",

    [string]$SinceSha = "",

    [string]$Title = "",

    [string]$Description = "",

    [string]$Repo = "ChrisMcKee1/scribe"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $RepoRoot
try {
    $Stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")

    if ($Target -eq "PullRequest") {
        if ($PullRequest -le 0) {
            throw "Specify -PullRequest <number> when -Target PullRequest is used."
        }

        $ViewerRaw = & gh api user 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "gh api user failed. Check 'gh auth status' and switch to ChrisMcKee1 before reviewing a PR."
        }
        $ViewerObj = $ViewerRaw | ConvertFrom-Json
        if ($ViewerObj.login -eq "chrismckee_microsoft") {
            throw "Active gh account is 'chrismckee_microsoft' (EMU), which cannot read $Repo. Ask the user to run 'gh auth switch --user ChrisMcKee1' first."
        }

        $RunId = "pr-$PullRequest-$Stamp"
        $CacheDir = Join-Path $RepoRoot "artifacts\scribe-code-review\$RunId"
        New-Item -ItemType Directory -Force -Path $CacheDir, (Join-Path $CacheDir "prompts") | Out-Null

        @{ login = [string]$ViewerObj.login } | ConvertTo-Json -Compress | Set-Content (Join-Path $CacheDir "viewer.json") -Encoding utf8NoBOM

        $MetaRaw = & gh pr view $PullRequest --repo $Repo --json number,title,body,author,baseRefName,headRefName,headRefOid,files,labels,state
        if ($LASTEXITCODE -ne 0) {
            throw "gh pr view $PullRequest failed (exit $LASTEXITCODE)."
        }
        $MetaRaw | Set-Content (Join-Path $CacheDir "metadata.json") -Encoding utf8NoBOM

        $PrDiff = & gh pr diff $PullRequest --repo $Repo
        if ($LASTEXITCODE -ne 0) {
            throw "gh pr diff $PullRequest failed (exit $LASTEXITCODE)."
        }
        $PrDiff | Set-Content (Join-Path $CacheDir "diff.patch") -Encoding utf8NoBOM

        # gh pr checks exits 8 when checks are pending and 1 when any check failed, while still printing valid JSON.
        $ChecksRaw = & gh pr checks $PullRequest --repo $Repo --json name,state,bucket,link 2>$null
        $ChecksExit = $LASTEXITCODE
        if (($ChecksExit -in @(0, 1, 8)) -and -not [string]::IsNullOrWhiteSpace(($ChecksRaw | Out-String))) {
            $ChecksRaw | Set-Content (Join-Path $CacheDir "checks.json") -Encoding utf8NoBOM
        } else {
            "[]" | Set-Content (Join-Path $CacheDir "checks.json") -Encoding utf8NoBOM
        }

        foreach ($Endpoint in @(
            @{ Path = "pulls/$PullRequest/reviews"; File = "reviews.json" },
            @{ Path = "pulls/$PullRequest/comments"; File = "pulls-comments.json" },
            @{ Path = "issues/$PullRequest/comments"; File = "issues-comments.json" }
        )) {
            $ApiOut = & gh api --paginate "repos/$Repo/$($Endpoint.Path)"
            if ($LASTEXITCODE -ne 0) {
                throw "gh api $($Endpoint.Path) failed (exit $LASTEXITCODE)."
            }
            $ApiOut | Set-Content (Join-Path $CacheDir $Endpoint.File) -Encoding utf8NoBOM
        }
    }
    else {
        $HeadSha = (& git rev-parse --verify $HeadRef 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($HeadSha)) {
            throw "Could not resolve HeadRef '$HeadRef'."
        }
        $ShortSha = $HeadSha.Substring(0, [Math]::Min(12, $HeadSha.Length))

        if ($Target -eq "Branch") {
            $MergeBase = (& git merge-base $BaseRef $HeadSha 2>$null | Out-String).Trim()
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($MergeBase)) {
                throw "git merge-base '$BaseRef' '$HeadSha' failed. Ensure '$BaseRef' exists locally rather than falling back to git diff HEAD."
            }
            $RunId = "branch-$ShortSha-$Stamp"
            $CacheDir = Join-Path $RepoRoot "artifacts\scribe-code-review\$RunId"
            New-Item -ItemType Directory -Force -Path $CacheDir, (Join-Path $CacheDir "prompts") | Out-Null

            $DiffOut = & git --no-pager diff "$MergeBase..$HeadSha"
            if ($LASTEXITCODE -ne 0) { throw "git diff $MergeBase..$HeadSha failed." }
            $DiffOut | Set-Content (Join-Path $CacheDir "diff.patch") -Encoding utf8NoBOM

            $ChangedPaths = @(& git --no-pager diff --name-only "$MergeBase..$HeadSha")
        }
        else {
            $RunId = "worktree-$ShortSha-$Stamp"
            $CacheDir = Join-Path $RepoRoot "artifacts\scribe-code-review\$RunId"
            New-Item -ItemType Directory -Force -Path $CacheDir, (Join-Path $CacheDir "prompts") | Out-Null

            $TrackedDiff = & git --no-pager diff HEAD
            if ($LASTEXITCODE -ne 0) { throw "git diff HEAD failed." }

            $StatusLines = @(& git status --porcelain -uall)
            $StatusLines | Set-Content (Join-Path $CacheDir "status.txt") -Encoding utf8NoBOM

            $UntrackedChunks = New-Object System.Collections.Generic.List[string]
            $UntrackedPaths = New-Object System.Collections.Generic.List[string]
            foreach ($Line in $StatusLines) {
                if ($Line -match '^\?\?\s+(.+)$') {
                    $RelPath = $Matches[1].Trim('"')
                    if ($RelPath -notmatch '^artifacts[\\/]' -and (Test-Path -LiteralPath $RelPath -PathType Leaf)) {
                        $UntrackedPaths.Add($RelPath)
                        $NoIdx = & git --no-pager diff --no-index -- NUL $RelPath 2>$null
                        if ($NoIdx) {
                            $UntrackedChunks.Add(($NoIdx -join "`n"))
                        }
                    }
                }
            }

            $Combined = @()
            if ($TrackedDiff) { $Combined += ($TrackedDiff -join "`n") }
            if ($UntrackedChunks.Count -gt 0) {
                $UntrackedText = ($UntrackedChunks -join "`n")
                $UntrackedText | Set-Content (Join-Path $CacheDir "untracked.patch") -Encoding utf8NoBOM
                $Combined += $UntrackedText
            }
            ($Combined -join "`n") | Set-Content (Join-Path $CacheDir "diff.patch") -Encoding utf8NoBOM

            $ChangedPaths = @(& git --no-pager diff --name-only HEAD) + @($UntrackedPaths)
        }

        (& git --no-pager log --oneline -20 $HeadSha) | Set-Content (Join-Path $CacheDir "recent-commits.txt") -Encoding utf8NoBOM

        $EffectiveTitle = if (-not [string]::IsNullOrWhiteSpace($Title)) {
            $Title
        } else {
            (& git --no-pager log -1 --format=%s $HeadSha | Out-String).Trim()
        }

        $MetaObj = [ordered]@{
            target      = $Target
            title       = $EffectiveTitle
            body        = $Description
            baseRefName = if ($Target -eq "Branch") { $BaseRef } else { "HEAD" }
            headRefName = (& git rev-parse --abbrev-ref $HeadRef | Out-String).Trim()
            headRefOid  = $HeadSha
            files       = @($ChangedPaths | Sort-Object -Unique | ForEach-Object { [ordered]@{ path = ($_ -replace '\\', '/') } })
            state       = "LOCAL"
            labels      = @()
        }
        ($MetaObj | ConvertTo-Json -Depth 6) | Set-Content (Join-Path $CacheDir "metadata.json") -Encoding utf8NoBOM
        "[]" | Set-Content (Join-Path $CacheDir "checks.json") -Encoding utf8NoBOM
        "[]" | Set-Content (Join-Path $CacheDir "reviews.json") -Encoding utf8NoBOM
        "[]" | Set-Content (Join-Path $CacheDir "pulls-comments.json") -Encoding utf8NoBOM
        "[]" | Set-Content (Join-Path $CacheDir "issues-comments.json") -Encoding utf8NoBOM
        "No prior review comments." | Set-Content (Join-Path $CacheDir "prior-comments-digest.md") -Encoding utf8NoBOM
    }

    if (-not [string]::IsNullOrWhiteSpace($SinceSha)) {
        $ResolvedSince = (& git rev-parse --verify $SinceSha 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($ResolvedSince)) {
            throw "Could not resolve -SinceSha '$SinceSha'."
        }
        $DeltaTarget = if ($Target -eq "WorkingTree") { @($ResolvedSince) } else { @("$ResolvedSince..$HeadRef") }
        $DeltaDiff = & git --no-pager diff @DeltaTarget
        if ($LASTEXITCODE -ne 0) {
            throw "git diff for SinceSha '$SinceSha' failed."
        }
        $DeltaDiff | Set-Content (Join-Path $CacheDir "delta.patch") -Encoding utf8NoBOM
    }

    Write-Output $CacheDir
}
finally {
    Pop-Location
}
