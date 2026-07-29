#requires -Version 7.0
<#
.SYNOPSIS
    Generates spoken-audio fixtures for the ASR smoke test using the Windows speech engine.

.DESCRIPTION
    The ASR check needs real speech, and committing WAV files would put binary blobs in the repo
    that are awkward to review and version. Windows already ships a text-to-speech engine (SAPI) on
    every SKU including Arm64, so the fixtures are synthesised at test time instead.

    SAPI is used through COM rather than the System.Speech managed assembly, because that assembly
    is .NET Framework only and is not loadable from PowerShell 7.

    The output is 16 kHz 16-bit mono, which is exactly what the dictation pipeline captures, so the
    fixtures exercise the same code path as a real recording with no resampling in between.

.EXAMPLE
    ./scripts/New-SpeechFixtures.ps1 -OutputDir artifacts/asr-fixtures
#>
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/asr-fixtures')
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
