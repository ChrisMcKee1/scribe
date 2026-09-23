#requires -Version 7.0
<#
.SYNOPSIS
    Regenerates the base speech phrases used by the AsrCheck scenario suite.

.DESCRIPTION
    `dotnet run --project tools/Scribe.AsrCheck -- --scenarios` derives every advanced scenario (device
    formats, noise, reverb, timing, long dictation, silence auto-stop, lifecycle, history storage) from a
    small set of committed base phrases. This script produces those phrases. It writes 16 kHz 16-bit mono
    PCM, the format the dictation pipeline hands to the recognizer, spread across the three installed
    en-US voices so the suite is not a single-speaker test.

    The output is COMMITTED under tests/fixtures/speech for the same reason as the smoke fixtures: SAPI
    fails with 0x8004503A on the headless x64 and Arm64 GitHub runners, so CI cannot synthesize speech.

    These phrases have their own manifest, scenario-fixtures.json. They are deliberately NOT added to
    fixtures.json, which is the smoke gate that `dotnet run --project tools/Scribe.AsrCheck` asserts on
    at a 0.6 word overlap: the scenario set includes a sentence with numbers and a time, which Scribe's
    editorial rules legitimately rewrite ("three thirty" becomes "3.30") and which would blunt that
    threshold. The scenario suite reports that phrase and never asserts on it ("asserted": false).
    Keeping the manifests apart also means rerunning New-SpeechFixtures.ps1, which rewrites
    fixtures.json from its own list, can never drop these entries, and rerunning this script never
    touches the four smoke fixtures.

    Voices: Microsoft David and Zira are SAPI desktop voices. Microsoft Mark ships only as a OneCore voice,
    so it is selected from the Speech_OneCore token category; SAPI's COM automation can speak with a
    OneCore token even though its default voice list does not show it. Synthesis goes to a file stream
    only; nothing is played on the speakers and no microphone is used.

    Run this only to add or change a phrase, then commit the WAVs and the manifest together.

.EXAMPLE
    ./scripts/New-ScenarioFixtures.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/fixtures/speech')
)

$ErrorActionPreference = 'Stop'

# SpeechAudioFormatType.SAFT16kHz16BitMono, and SpeechStreamFileMode.SSFMCreateForWrite.
$SAFT16kHz16BitMono = 18
$SSFMCreateForWrite = 3

# Committed audio stays small: the repository is public and every clone pays for it.
$MaxTotalBytes = 3MB

# Roles tell the scenario suite how to use a phrase:
#   dictionary - contains a term the suite's test dictionary canonicalizes
#   snippet    - is exactly a snippet trigger phrase
#   speech     - ordinary dictation (question, list, long passage)
#   numbers    - contains numbers and a time; reported, never asserted
# 'asserted' = $false keeps a phrase out of every pass/fail gate.
$phrases = [ordered]@{
    'dict-azure-devops'   = @{ Voice = 'Zira';  Role = 'dictionary'; Asserted = $true;  Text = 'We track every bug in azure devops and review the board on Monday morning.' }
    'dict-scribe'         = @{ Voice = 'Mark';  Role = 'dictionary'; Asserted = $true;  Text = 'Scribe types whatever I say into the window that has focus.' }
    'dict-github-copilot' = @{ Voice = 'David'; Role = 'dictionary'; Asserted = $true;  Text = 'Ask github copilot to explain why the build is failing.' }
    'dict-kubernetes'     = @{ Voice = 'Zira';  Role = 'dictionary'; Asserted = $true;  Text = 'The kubernetes cluster restarted overnight after the upgrade.' }
    'snippet-signature'   = @{ Voice = 'Mark';  Role = 'snippet';    Asserted = $true;  Text = 'insert my signature' }
    'snippet-address'     = @{ Voice = 'David'; Role = 'snippet';    Asserted = $true;  Text = 'insert my address' }
    'question'            = @{ Voice = 'Zira';  Role = 'speech';     Asserted = $true;  Text = 'Can you send me the latest version of the design document?' }
    'list'                = @{ Voice = 'Mark';  Role = 'speech';     Asserted = $true;  Text = 'We need milk, eggs, bread, coffee, and a bag of apples.' }
    'numbers-time'        = @{ Voice = 'David'; Role = 'numbers';    Asserted = $false; Text = 'The meeting moved to three thirty on Tuesday, and forty two people signed up for the workshop.' }
    'long-passage'        = @{ Voice = 'Zira';  Role = 'speech';     Asserted = $true;  Text = 'Good morning everyone. Before we start the planning meeting, I want to thank the whole team for the work on the release last week. The installer is smaller, dictation feels faster, and the support queue is finally quiet. Today we will walk through the open issues, agree on the priorities for the next sprint, and decide who will own the documentation updates.' }
}

function Get-VoiceTokens {
    $voice = New-Object -ComObject SAPI.SpVoice
    $tokens = @()
    foreach ($token in $voice.GetVoices()) { $tokens += $token }

    $oneCore = New-Object -ComObject SAPI.SpObjectTokenCategory
    try {
        $oneCore.SetId('HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices', $false)
        foreach ($token in $oneCore.EnumerateTokens()) { $tokens += $token }
    }
    catch {
        Write-Warning "OneCore voices are unavailable: $($_.Exception.Message)"
    }

    return $tokens
}

# Desktop tokens are preferred when both exist: they are the classic SAPI voices the smoke fixtures
# were generated with. Mark has no desktop token, so it always resolves to OneCore.
function Resolve-Voice {
    param([object[]]$Tokens, [string]$Name)

    $desktop = $Tokens | Where-Object { $_.GetDescription() -like "Microsoft $Name Desktop*" } | Select-Object -First 1
    if ($desktop) { return $desktop }

    $any = $Tokens | Where-Object { $_.GetDescription() -like "Microsoft $Name*" } | Select-Object -First 1
    if ($any) { return $any }

    throw "Voice 'Microsoft $Name' is not installed. The committed fixtures name their voice, so a substitute would make the manifest lie."
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$tokens = Get-VoiceTokens
$voice = New-Object -ComObject SAPI.SpVoice
$manifest = [ordered]@{}
$totalBytes = 0

foreach ($name in $phrases.Keys) {
    $entry = $phrases[$name]
    $token = Resolve-Voice -Tokens $tokens -Name $entry.Voice
    $voice.Voice = $token

    $path = Join-Path $OutputDir "$name.wav"
    if (Test-Path $path) { Remove-Item $path -Force }

    $stream = New-Object -ComObject SAPI.SpFileStream
    try {
        $stream.Format.Type = $SAFT16kHz16BitMono
        $stream.Open($path, $SSFMCreateForWrite, $false)
        $voice.AudioOutputStream = $stream
        $null = $voice.Speak($entry.Text, 0)
    }
    finally {
        $stream.Close()
        # Release the output binding before the next iteration so the file handle is not held.
        $voice.AudioOutputStream = $null
    }

    $size = (Get-Item $path).Length
    if ($size -lt 8KB) { throw "Generated fixture $name.wav is only $size bytes; the speech engine produced no audio." }
    $totalBytes += $size

    # 16 kHz, 16-bit mono is 32,000 bytes per second; the 44-byte header does not matter at this scale.
    $seconds = [math]::Round(($size - 44) / 32000.0, 2)
    $manifest[$name] = [ordered]@{
        file     = "$name.wav"
        text     = $entry.Text
        voice    = $token.GetDescription()
        role     = $entry.Role
        asserted = $entry.Asserted
        seconds  = $seconds
    }
    Write-Host ("    {0,-20} {1,9:N0} bytes {2,6:N2}s  {3}" -f $name, $size, $seconds, $token.GetDescription())
}

if ($totalBytes -gt $MaxTotalBytes) {
    throw ("Scenario fixtures total {0:N0} bytes, over the {1:N0} byte budget for committed audio. Shorten a phrase." -f $totalBytes, $MaxTotalBytes)
}

$manifestPath = Join-Path $OutputDir 'scenario-fixtures.json'
$manifest | ConvertTo-Json -Depth 4 | Set-Content $manifestPath -Encoding utf8
Write-Host ("==> {0} scenario fixtures ({1:N0} bytes) written to {2}" -f $manifest.Count, $totalBytes, $OutputDir) -ForegroundColor Green
