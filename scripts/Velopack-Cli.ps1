#requires -Version 7.0
<#
.SYNOPSIS
    Keeps the Velopack CLI (vpk) at the version of the Velopack package the app references.

.DESCRIPTION
    docs.velopack.io recommends running the vpk version that matches the Velopack NuGet package the
    app references. An unpinned `dotnet tool install -g vpk` takes whatever is newest on the feed,
    which on a clean hosted runner can be ahead of the version in Directory.Packages.props.

    The installed version is read first with `dotnet tool list`, which works offline, and the feed is
    contacted only when vpk is missing or at a different version. A maintainer who already has the
    right vpk can therefore pack offline or while the feed is down.

    Dot-sourced by build/pack.ps1. -ToolPath lets the same logic run against a private tool
    directory instead of the user's global tools.
#>

function Get-ScribeVpkVersion {
    param([string]$ToolPath)

    $listing = if ($ToolPath) { dotnet tool list --tool-path $ToolPath 2>$null } else { dotnet tool list -g 2>$null }
    $match = $listing | Select-String -Pattern '^\s*vpk\s+(\S+)' | Select-Object -First 1
    if ($match) { return $match.Matches[0].Groups[1].Value }
    return $null
}

function Sync-ScribeVelopackCli {
    param(
        [Parameter(Mandatory)][string]$Version,
        [string]$ToolPath
    )

    $installed = Get-ScribeVpkVersion -ToolPath $ToolPath
    if ($installed -eq $Version) {
        Write-Host "==> Velopack CLI (vpk) $Version is already installed." -ForegroundColor Green
        return
    }

    # Typed as an array on purpose: an if expression unrolls a one-element array to a plain string,
    # and splatting a string passes it one character at a time ("-g" arrived as "-" and "g").
    [string[]]$scope = if ($ToolPath) { '--tool-path', $ToolPath } else { '-g' }
    $action = if ($installed) { "Moving vpk $installed to $Version" } else { "Installing vpk $Version" }
    Write-Host "==> $action to match the Velopack package..." -ForegroundColor Yellow

    # `dotnet tool update` installs when the tool is missing, and --allow-downgrade lets it move a
    # newer vpk back to the pinned version.
    dotnet tool update @scope vpk --version $Version --allow-downgrade
    if ($LASTEXITCODE -ne 0) { throw "Could not install vpk $Version to match the Velopack package." }
}
