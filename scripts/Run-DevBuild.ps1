<#
.SYNOPSIS
    Builds the working tree and runs it in place of the installed Scribe, for live testing before
    a release is cut.

.DESCRIPTION
    Stops any running Scribe (Store MSIX, Velopack install, or a previous dev run), builds the
    solution including the x64-only overlay, verifies the ASR models are present, then launches the
    freshly built app detached so it keeps running after this shell exits.

    Nothing is installed and no version is bumped. The installed copy is untouched on disk: it is
    only stopped, so relaunching it from the Start menu restores normal behavior.

.PARAMETER Configuration
    Build configuration. Debug (default) gives readable logs; Release matches shipping behavior.

.PARAMETER Settings
    Open the settings window on launch instead of starting in the tray.

.PARAMETER NoBuild
    Skip the build and just relaunch what is already compiled.

.EXAMPLE
    ./scripts/Run-DevBuild.ps1
    ./scripts/Run-DevBuild.ps1 -Settings
    ./scripts/Run-DevBuild.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Settings,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo

function Write-Step([string]$text) { Write-Host "`n=== $text" -ForegroundColor Cyan }

# Newest matching executable for the configuration being run. Newest rather than first because both
# platform layouts can be present at once on a tree that has been built each way.
function Find-Output([string]$relativeBin, [string]$exeName) {
    $root = Join-Path $repo $relativeBin
    if (-not (Test-Path $root)) { return $null }
    Get-ChildItem -Path $root -Recurse -Filter $exeName -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*$([IO.Path]::DirectorySeparatorChar)$Configuration$([IO.Path]::DirectorySeparatorChar)*" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

try {
    Write-Step 'Stopping any running Scribe'
    # Query first, then stop by literal id: a name-based kill would also match an unrelated process,
    # and the overlay must go down with the engine or the pill outlives its pipe server.
    $running = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -in @('Scribe', 'Scribe.Overlay') })

    if ($running.Count -eq 0) {
        Write-Host '  nothing running.'
    }
    foreach ($p in $running) {
        $source =
            if ($p.Path -like '*WindowsApps*') { 'Store MSIX' }
            elseif ($p.Path -like "$env:LOCALAPPDATA\Scribe\*") { 'Velopack install' }
            else { 'dev build' }
        Write-Host "  stopping $($p.ProcessName) pid=$($p.Id) ($source)"
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    if ($running.Count -gt 0) {
        Start-Sleep -Milliseconds 900
    }

    if (-not $NoBuild) {
        Write-Step "Building ($Configuration)"
        # The overlay is x64-only and is not covered by a plain solution build's default platform,
        # so build it explicitly first or the app launches against a stale pill.
        dotnet build src/Scribe.Overlay/Scribe.Overlay.csproj -c $Configuration -p:Platform=x64 --nologo -v:m
        if ($LASTEXITCODE -ne 0) { throw "Overlay build failed ($LASTEXITCODE)." }

        dotnet build Scribe.slnx -c $Configuration --nologo -v:m
        if ($LASTEXITCODE -ne 0) { throw "Solution build failed ($LASTEXITCODE)." }
    }

    Write-Step 'Checking runtime prerequisites'
    $models = Join-Path $repo 'src/Scribe.App/models'
    $required = @('encoder.int8.onnx', 'decoder.int8.onnx', 'joiner.int8.onnx', 'tokens.txt', 'silero_vad_v5.onnx')
    $missing = $required | Where-Object { -not (Test-Path (Join-Path $models $_)) }
    if ($missing) {
        throw "Missing model files: $($missing -join ', '). Run: pwsh ./scripts/Download-Models.ps1"
    }
    Write-Host "  models OK ($models)"

    # Resolved by search rather than by a literal path. The TFM carries the Windows SDK version and
    # moves whenever that is bumped, and the overlay lands under bin/x64 or bin depending on whether
    # the build passed -p:Platform=x64. A hardcoded path went stale against both and failed the run
    # AFTER the stop and the build, which leaves the machine with no Scribe running at all.
    $overlay = Find-Output 'src/Scribe.Overlay/bin' 'Scribe.Overlay.exe'
    if (-not $overlay) {
        throw "Overlay executable not found under src/Scribe.Overlay/bin for $Configuration. Build first."
    }
    Write-Host "  overlay OK"

    $app = Find-Output 'src/Scribe.App/bin' 'Scribe.exe'
    if (-not $app) {
        throw "App executable not found under src/Scribe.App/bin for $Configuration. Build first."
    }

    Write-Step 'Launching dev build'
    # SCRIBE_OVERLAY_EXE is the first resolution step in OverlayProcessClient, so an explicit path
    # removes any doubt about whether the dev pill or an installed one is in play.
    $env:SCRIBE_OVERLAY_EXE = $overlay

    $args = @()
    if ($Settings) { $args += '--settings' }

    $proc = Start-Process -FilePath $app -ArgumentList $args -PassThru
    Start-Sleep -Milliseconds 1200

    if ($proc.HasExited) {
        throw "Scribe exited immediately (code $($proc.ExitCode)). Check the log below."
    }

    Write-Host "  running: pid=$($proc.Id)" -ForegroundColor Green
    Write-Host "  exe    : $app"
    Write-Host "  overlay: $overlay"

    $log = Join-Path $env:LOCALAPPDATA "ScribeData\logs\scribe-$(Get-Date -Format yyyyMMdd).log"
    Write-Host "`n  live log: $log"
    Write-Host "  tail it : Get-Content '$log' -Wait -Tail 40"
    Write-Host "`n  When you are done, close Scribe from the tray and relaunch the installed"
    Write-Host "  version from the Start menu. Nothing was overwritten." -ForegroundColor Yellow
}
finally {
    Pop-Location
}
