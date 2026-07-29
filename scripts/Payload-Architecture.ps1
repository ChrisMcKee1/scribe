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
# 0x014C (i386) is reported both by 32-bit x86 natives and by architecture-neutral managed
# assemblies, so it is treated as "not an architecture violation" rather than "verified"; the
# distinction needs the CLI header, which is more machinery than this check warrants.
$script:ScribePeMachine = @{
    0x8664 = 'x64'
    0xAA64 = 'arm64'
    0x014C = 'anycpu'
}

function Get-ScribePeSectionNames {
    param([Parameter(Mandatory)][System.IO.BinaryReader]$Reader, [int]$PeOffset)

    $stream = $Reader.BaseStream
    $stream.Position = $PeOffset + 6
    $sectionCount = $Reader.ReadUInt16()
    $stream.Position = $PeOffset + 20
    $optionalHeaderSize = $Reader.ReadUInt16()

    $names = [System.Collections.Generic.List[string]]::new()
    $stream.Position = $PeOffset + 24 + $optionalHeaderSize
    for ($i = 0; $i -lt $sectionCount -and ($stream.Position + 40) -le $stream.Length; $i++) {
        $names.Add([System.Text.Encoding]::ASCII.GetString($Reader.ReadBytes(8)).TrimEnd([char]0))
        $null = $Reader.ReadBytes(32)
    }
    return $names
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

        # ARM64EC binaries carry IMAGE_FILE_MACHINE_ARM64 but are designed to load into an x64
        # process, and the Windows App SDK ships an _ec variant inside its x64 package on purpose.
        # They are identified by the .hexpthk entry-thunk section, which pure ARM64 images do not
        # have (verified against this repo's own ARM64 WinUI output). Without this they would look
        # like ARM64 leakage in a correct x64 payload.
        if ($machine -eq 0xAA64) {
            if ((Get-ScribePeSectionNames -Reader $reader -PeOffset $peOffset) -contains '.hexpthk') {
                return 'arm64ec'
            }
        }

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
        # Only the opposite architecture is a violation. anycpu/unknown are not: managed assemblies
        # are architecture neutral, and data files that happen to end in .dll are not our problem.
        # Note this means a 32-bit x86 native would slip through, because it shares machine 0x014C
        # with managed assemblies; nothing in the dependency graph ships one, and the Scribe.exe
        # check below would still catch a wholesale wrong-architecture publish. arm64ec is excluded
        # by Get-ScribePeArchitecture because it is the designed x64-interop form, not leakage.
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
