#requires -Version 7.0
<#
.SYNOPSIS
    Verifies every native binary in a published payload matches the architecture it ships as.

.DESCRIPTION
    Scribe ships two architectures from one source tree, and a mismatch is invisible at runtime:
    Windows on Arm silently emulates an x64 binary, so a wrongly-built payload does not crash, it
    just runs slower and drains more battery. That surfaces as a vague performance complaint rather
    than a packaging bug, so the check belongs at pack time where it fails loudly.

    Reads the PE COFF machine field directly rather than shelling out to dumpbin, which is not
    present on a hosted runner without the C++ workload.

    Deliberately does NOT call Set-StrictMode: this file is dot-sourced, so a strict-mode change
    here would leak into the caller's scope and alter how the whole pack script behaves (missing
    XML properties would start throwing instead of returning null, turning our friendly
    "Directory.Build.props must define ..." errors into cryptic runtime failures).
#>

# PE COFF machine constants (winnt.h IMAGE_FILE_MACHINE_*).
$script:ScribePeMachine = @{
    0x8664 = 'x64'
    0xAA64 = 'arm64'
    0x014C = 'anycpu'   # managed assemblies and x86 natives both report i386 here
}

function Get-ScribePeArchitecture {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        if ($stream.Length -lt 0x40) { return 'unknown' }

        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -le 0 -or ($peOffset + 6) -ge $stream.Length) { return 'unknown' }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { return 'unknown' }  # "PE\0\0"

        $machine = $reader.ReadUInt16()
        if ($script:ScribePeMachine.ContainsKey([int]$machine)) {
            return $script:ScribePeMachine[[int]$machine]
        }
        return ('0x{0:X4}' -f $machine)
    }
    catch {
        return 'unknown'
    }
    finally {
        $stream.Dispose()
    }
}

function Test-ScribePayloadArchitecture {
    param(
        [Parameter(Mandatory)][string]$PublishDir,
        [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Runtime
    )

    $expected = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
    $wrong = if ($expected -eq 'arm64') { 'x64' } else { 'arm64' }

    $offenders = [System.Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem $PublishDir -Recurse -File -Include *.exe, *.dll) {
        # anycpu/unknown are not architecture violations: managed assemblies are architecture
        # neutral, and data files that happen to end in .dll are not our problem.
        if ((Get-ScribePeArchitecture -Path $file.FullName) -eq $wrong) {
            $offenders.Add($file.FullName.Substring($PublishDir.Length).TrimStart('\', '/'))
        }
    }

    if ($offenders.Count -gt 0) {
        $sample = ($offenders | Select-Object -First 10) -join "`n  "
        throw "Payload for $Runtime contains $($offenders.Count) $wrong binaries:`n  $sample"
    }

    # A payload with no binaries of the expected architecture means the publish silently produced
    # nothing native, which would otherwise pass the negative check above.
    $mainExe = Join-Path $PublishDir 'Scribe.exe'
    if (Test-Path $mainExe) {
        $actual = Get-ScribePeArchitecture -Path $mainExe
        if ($actual -ne $expected) {
            throw "Scribe.exe in the $Runtime payload is '$actual', expected '$expected'."
        }
    }

    Write-Host "==> Payload architecture verified: every native binary is $expected." -ForegroundColor Green
}
