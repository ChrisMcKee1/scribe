#requires -Version 7.0
<#
.SYNOPSIS
    Regenerates the spoken-audio fixtures used by the ASR smoke test.

.DESCRIPTION
    The ASR check needs real speech. These fixtures are generated once and COMMITTED under
    tests/fixtures/speech, rather than produced on demand, because the Windows speech engine is not
    usable on a headless CI runner: SAPI fails with 0x8004503A on both the x64 and Arm64 GitHub
    runners after successfully enumerating a voice, and the audio stack is not something the build
    should depend on. Committed WAVs also make the check deterministic across machines.

    Run this only to add or change a phrase, then commit the result.

    SAPI is used through COM rather than the System.Speech managed assembly, because that assembly
    is .NET Framework only and is not loadable from PowerShell 7.

    The output is 16 kHz 16-bit mono, which is exactly what the dictation pipeline captures, so the
    fixtures exercise the same code path as a real recording with no resampling in between.

.EXAMPLE
    ./scripts/New-SpeechFixtures.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/fixtures/speech')
)

$ErrorActionPreference = 'Stop'

# SpeechAudioFormatType.SAFT16kHz16BitMono. The neighbouring values are different sample rates, so
# a wrong constant here yields audio the recogniser silently handles worse rather than an error.
$SAFT16kHz16BitMono = 18
$SSFMCreateForWrite = 3

# Kept deliberately plain: the point is to prove the native ASR stack decodes on this architecture,
# not to benchmark accuracy. Phrases avoid numbers, dates and times on purpose, because Scribe's
# editorial rules legitimately rewrite those ("three thirty" becomes "3.30"), which would score as a
# mismatch and blunt the threshold that is meant to catch a genuinely broken native.
$phrases = [ordered]@{
    'greeting'    = 'Hello, this is a test of the Scribe dictation system.'
    'pangram'     = 'The quick brown fox jumps over the lazy dog.'
    'sentence'    = 'Please send the report to the team before Friday afternoon.'
    'longer'      = 'The engineer opened the laptop, checked the microphone, and started dictating a message.'
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$voice = New-Object -ComObject SAPI.SpVoice
Write-Host "==> Using voice: $($voice.Voice.GetDescription())" -ForegroundColor Cyan

$manifest = [ordered]@{}
foreach ($name in $phrases.Keys) {
    $text = $phrases[$name]
    $path = Join-Path $OutputDir "$name.wav"
    if (Test-Path $path) { Remove-Item $path -Force }

    $stream = New-Object -ComObject SAPI.SpFileStream
    try {
        $stream.Format.Type = $SAFT16kHz16BitMono
        $stream.Open($path, $SSFMCreateForWrite, $false)
        $voice.AudioOutputStream = $stream
        $null = $voice.Speak($text, 0)
    }
    finally {
        $stream.Close()
        # Release the output binding before the next iteration so the file handle is not held.
        $voice.AudioOutputStream = $null
    }

    $size = (Get-Item $path).Length
    if ($size -lt 8KB) { throw "Generated fixture $name.wav is only $size bytes; the speech engine produced no audio." }

    $manifest[$name] = @{ file = "$name.wav"; text = $text }
    Write-Host ("    {0,-12} {1,9:N0} bytes  `"{2}`"" -f $name, $size, $text)
}

$manifestPath = Join-Path $OutputDir 'fixtures.json'
$manifest | ConvertTo-Json -Depth 4 | Set-Content $manifestPath -Encoding utf8
Write-Host "==> $($manifest.Count) fixtures written to $OutputDir" -ForegroundColor Green
