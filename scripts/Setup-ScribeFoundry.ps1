<#
.SYNOPSIS
    Sets up a Microsoft Foundry resource, project and chat model deployment for Scribe's AI cleanup.

.DESCRIPTION
    Run it with no arguments and answer the prompts. It signs you in, lets you pick the Azure
    subscription (your Visual Studio credits work fine), measures which regions are actually fast
    from where you are sitting, checks the model quota in those regions, then creates:

      - a resource group
      - a Microsoft Foundry resource (kind AIServices, project management enabled, custom subdomain)
      - a Foundry project
      - a chat model deployment
      - a dedicated Entra identity (service principal) for Scribe, with the Foundry User role

    It finishes by printing everything Scribe asks for and copying the endpoint to your clipboard.

    The service principal is created by default and on purpose. The Azure CLI has one active
    account at a time, so if you belong to more than one tenant, which most people at Microsoft do,
    "sign in with az login" quietly means "whichever tenant az happened to land on last". That
    produces AADSTS700016 errors weeks later when you have forgotten you ever ran az login. A
    service principal pins Scribe to exactly one identity in exactly one tenant, permanently.

    If your tenant does not let you register applications, or you cannot assign roles on the
    resource, the script says so plainly, tells you what to ask your administrator for, and falls
    back to az login sign-in so you still end up with a working setup.

    This deliberately creates a FOUNDRY resource, not a classic "Azure AI services" resource. They
    look similar in the portal and are not interchangeable: only the Foundry shape gives you the
    project endpoint and the Foundry role model that Scribe expects.

.PARAMETER SubscriptionId
    Skip the subscription picker and use this subscription.

.PARAMETER Location
    Skip the region picker and use this region, for example eastus2.

.PARAMETER ResourceGroup
    Resource group to create or reuse. Defaults to rg-scribe-ai.

.PARAMETER ResourceName
    Foundry resource name. Must be globally unique because it becomes your subdomain. Defaults to
    a generated name based on your sign-in alias.

.PARAMETER ProjectName
    Foundry project name. Defaults to scribe.

.PARAMETER Model
    Model to deploy. Defaults to gpt-5.6-terra.

.PARAMETER ModelVersion
    Pin a specific model version. Defaults to the newest version the region offers.

.PARAMETER Sku
    Deployment type. Defaults to DataZoneStandard, falling back to GlobalStandard if the region
    has no data zone quota for the model.

.PARAMETER Capacity
    Rate limit in thousands of tokens per minute. Defaults to 100, trimmed to whatever quota you
    actually have left. Standard deployments bill per token, so a larger number costs nothing
    extra, it just raises the ceiling before you get throttled.

.PARAMETER ServicePrincipalName
    Name for the Entra app registration. Defaults to Scribe-AI-Cleanup-<your alias>. Reused if it
    already exists, so re-running the script does not litter your tenant with duplicates.

.PARAMETER SecretYears
    How long the client secret lasts before it expires. Defaults to 1. Microsoft recommends under
    2, and the maximum Entra allows is 2.

.PARAMETER SkipServicePrincipal
    Do not create a dedicated identity. Scribe will sign in with your az login instead. Only choose
    this if you are certain you only ever sign in to one tenant.

.PARAMETER WhatIf
    Print every step and every Azure command without changing anything.

.EXAMPLE
    irm https://raw.githubusercontent.com/ChrisMcKee1/scribe/main/scripts/Setup-ScribeFoundry.ps1 | iex

    The no-clone path. Answer the prompts.

.EXAMPLE
    .\Setup-ScribeFoundry.ps1

    The normal case if you do have the repo. Answer the prompts.

.EXAMPLE
    $s = irm https://raw.githubusercontent.com/ChrisMcKee1/scribe/main/scripts/Setup-ScribeFoundry.ps1
    & ([scriptblock]::Create($s)) -Location eastus2 -Model gpt-5.4

    Piping into iex cannot pass parameters, so build a scriptblock when you need them.

.EXAMPLE
    .\Setup-ScribeFoundry.ps1 -Location eastus2 -Model gpt-5.4

    Skip the region picker and deploy a different model.

.EXAMPLE
    .\Setup-ScribeFoundry.ps1 -SkipServicePrincipal

    Use your az login instead of a dedicated identity.

.LINK
    https://github.com/ChrisMcKee1/scribe/blob/main/docs/foundry-setup.md
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SubscriptionId,
    [string]$Location,
    [string]$ResourceGroup = 'rg-scribe-ai',
    [string]$ResourceName,
    [string]$ProjectName = 'scribe',
    [string]$Model = 'gpt-5.6-terra',
    [string]$ModelVersion,
    [ValidateSet('DataZoneStandard', 'GlobalStandard', 'Standard')]
    [string]$Sku = 'DataZoneStandard',
    [ValidateRange(1, 5000)]
    [int]$Capacity = 100,
    [string]$ServicePrincipalName,
    [ValidateRange(1, 2)]
    [int]$SecretYears = 1,
    [switch]$SkipServicePrincipal
)

# Everything below runs inside a scriptblock on purpose, so that this script is safe to pipe
# straight into iex from a URL. Two things would otherwise go badly wrong for anyone who never
# clones the repo:
#   1. "exit" inside an iex'd string terminates the HOST session, so a friendly early bail-out
#      would slam the user's PowerShell window shut, taking the explanation with it. Inside a
#      scriptblock, "return" just ends the setup and leaves them at a prompt to read the message.
#   2. Set-StrictMode and $ErrorActionPreference would leak into the caller's session and change
#      how their unrelated commands behave for the rest of the day.
& {
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'

    # Foundry User. Assign by ID, not by name: the Foundry roles were renamed from "Azure AI *" and a
    # given portal or CLI build may still show either label while the rename rolls out.
    $FoundryUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

    # Where this script lives, so the error paths can show a re-run command that works for the people
    # who got here through "irm ... | iex" and have no local copy to invoke.
    $ScriptUrl = 'https://raw.githubusercontent.com/ChrisMcKee1/scribe/main/scripts/Setup-ScribeFoundry.ps1'

    # Minimum az version that understands --allow-project-management, which is what makes the resource
    # a Foundry resource rather than a classic Azure AI services account.
    $MinimumAzVersion = [version]'2.73.0'

    # Candidate regions to time and quota-check. Kept to regions that actually host frontier chat
    # models, because a fast region with no models in it is not a useful answer.
    $CandidateRegions = @(
        @{ Name = 'eastus';         Display = 'East US (Virginia)' }
        @{ Name = 'eastus2';        Display = 'East US 2 (Virginia)' }
        @{ Name = 'centralus';      Display = 'Central US (Iowa)' }
        @{ Name = 'southcentralus'; Display = 'South Central US (Texas)' }
        @{ Name = 'northcentralus'; Display = 'North Central US (Illinois)' }
        @{ Name = 'westus';         Display = 'West US (California)' }
        @{ Name = 'westus3';        Display = 'West US 3 (Arizona)' }
        @{ Name = 'canadaeast';     Display = 'Canada East (Quebec)' }
        @{ Name = 'swedencentral';  Display = 'Sweden Central' }
        @{ Name = 'westeurope';     Display = 'West Europe (Netherlands)' }
        @{ Name = 'uksouth';        Display = 'UK South (London)' }
        @{ Name = 'francecentral';  Display = 'France Central' }
        @{ Name = 'australiaeast';  Display = 'Australia East (Sydney)' }
        @{ Name = 'japaneast';      Display = 'Japan East (Tokyo)' }
    )

    $script:StepNumber = 0

    function Write-Step {
        param([string]$Message)
        $script:StepNumber++
        Write-Host ''
        Write-Host ("  {0}. {1}" -f $script:StepNumber, $Message) -ForegroundColor Cyan
    }

    function Write-Detail {
        param([string]$Message)
        Write-Host "     $Message" -ForegroundColor DarkGray
    }

    function Write-Good {
        param([string]$Message)
        Write-Host "     $Message" -ForegroundColor Green
    }

    function Write-Warn {
        param([string]$Message)
        Write-Host "     $Message" -ForegroundColor Yellow
    }

    function Write-Fail {
        param([string]$Message)
        Write-Host ''
        Write-Host "  $Message" -ForegroundColor Red
    }

    # Confirmations for things this script actually changed. Under -WhatIf nothing was changed, and the
    # [WhatIf] line above already showed the plan, so repeating it in the past tense tells a first-time
    # user their resources exist when they do not.
    function Write-Created {
        param([string]$Message)
        if ($WhatIfPreference) { return }
        Write-Good $Message
    }

    # Set-StrictMode makes reading an absent property a terminating error, so any field an az command
    # only sometimes returns has to be probed rather than read. "az ad app credential reset" omitting
    # "tenant" is the case that matters here, and it is exactly the case the caller wants to recover
    # from, so reading it directly would crash precisely when the fallback was needed.
    function Test-HasValue {
        param($Object, [Parameter(Mandatory = $true)][string]$Name)
        if (-not $Object) { return $false }
        if (-not $Object.PSObject.Properties[$Name]) { return $false }
        return -not [string]::IsNullOrWhiteSpace([string]$Object.$Name)
    }

    # All az calls funnel through here so that -WhatIf can print them, failures carry the actual stderr
    # text instead of a bare exit code, and JSON parsing happens in exactly one place.
    function Invoke-Az {
        param(
            [Parameter(Mandatory = $true)][string[]]$Arguments,
            [switch]$AsJson,
            [switch]$AllowFailure,
            [switch]$AlwaysRun,
            [string]$WhatIfResult
        )

        $printable = 'az ' + ($Arguments -join ' ')
        $planningOnly = [bool]$WhatIfPreference

        if ($planningOnly -and -not $AlwaysRun) {
            Write-Host "     [WhatIf] $printable" -ForegroundColor Magenta
            if ($WhatIfResult) { return $WhatIfResult | ConvertFrom-Json }
            return $null
        }

        Write-Verbose $printable

        # WhatIf must not propagate to our own bookkeeping. Without this, the stderr redirect and the
        # temp file cleanup below emit "What if: Performing the operation Output to File" noise that
        # buries the actual plan.
        $WhatIfPreference = $false
        $stdErrFile = [System.IO.Path]::GetTempFileName()
        try {
            $output = & az @Arguments 2>$stdErrFile
            $exitCode = $LASTEXITCODE
            $stdErr = (Get-Content -LiteralPath $stdErrFile -Raw -ErrorAction SilentlyContinue)

            if ($exitCode -ne 0) {
                if ($AllowFailure) { return $null }
                $detail = if ($stdErr) { $stdErr.Trim() } else { ($output -join [Environment]::NewLine) }
                throw "Azure CLI failed (exit $exitCode).`n  Command: $printable`n  Azure said: $detail"
            }

            # Warnings on stderr are routine and are not failures, so surface them without stopping.
            if ($stdErr -and $stdErr.Trim()) { Write-Verbose $stdErr.Trim() }

            if (-not $AsJson) { return $output }

            $text = ($output -join [Environment]::NewLine).Trim()
            if (-not $text) { return $null }
            return $text | ConvertFrom-Json
        }
        finally {
            Remove-Item -LiteralPath $stdErrFile -Force -ErrorAction SilentlyContinue -WhatIf:$false
        }
    }

    function Read-Choice {
        param(
            [Parameter(Mandatory = $true)][string]$Prompt,
            [Parameter(Mandatory = $true)][array]$Items,
            [Parameter(Mandatory = $true)][scriptblock]$Label,
            [int]$DefaultIndex = 0
        )

        if ($Items.Count -eq 1) {
            Write-Detail ("Only one option, using it: " + (& $Label $Items[0]))
            return $Items[0]
        }

        Write-Host ''
        for ($i = 0; $i -lt $Items.Count; $i++) {
            $marker = if ($i -eq $DefaultIndex) { '*' } else { ' ' }
            $colour = if ($i -eq $DefaultIndex) { 'White' } else { 'Gray' }
            Write-Host ("   {0} [{1,2}] {2}" -f $marker, ($i + 1), (& $Label $Items[$i])) -ForegroundColor $colour
        }
        Write-Host ''

        while ($true) {
            $answer = Read-Host "$Prompt [Enter for $($DefaultIndex + 1)]"
            if ([string]::IsNullOrWhiteSpace($answer)) { return $Items[$DefaultIndex] }

            $parsed = 0
            if ([int]::TryParse($answer.Trim(), [ref]$parsed) -and $parsed -ge 1 -and $parsed -le $Items.Count) {
                return $Items[$parsed - 1]
            }
            Write-Warn "Type a number between 1 and $($Items.Count), or press Enter."
        }
    }

    function Confirm-Yes {
        param([string]$Prompt, [bool]$DefaultYes = $true)
        $suffix = if ($DefaultYes) { '[Y/n]' } else { '[y/N]' }
        while ($true) {
            $answer = (Read-Host "$Prompt $suffix").Trim()
            if ([string]::IsNullOrWhiteSpace($answer)) { return $DefaultYes }
            if ($answer -match '^(y|yes)$') { return $true }
            if ($answer -match '^(n|no)$') { return $false }
        }
    }

    # Measures a TCP connect to each region's Cognitive Services front door. This is the same idea as
    # the azurespeed.com latency table, minus the trip to a third-party website, and it measures the
    # path your own machine will actually take.
    function Measure-RegionLatency {
        param([Parameter(Mandatory = $true)][string]$Region, [int]$TimeoutMs = 2500)

        $best = [int]::MaxValue
        foreach ($attempt in 1..2) {
            $client = $null
            try {
                $client = New-Object System.Net.Sockets.TcpClient
                $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                $async = $client.BeginConnect("$Region.api.cognitive.microsoft.com", 443, $null, $null)
                if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) { continue }
                $client.EndConnect($async)
                $stopwatch.Stop()
                if ($stopwatch.ElapsedMilliseconds -lt $best) { $best = [int]$stopwatch.ElapsedMilliseconds }
            }
            catch { continue }
            finally { if ($client) { $client.Close() } }
        }

        if ($best -eq [int]::MaxValue) { return $null }
        return $best
    }

    function Get-ModelOffer {
        param(
            [Parameter(Mandatory = $true)][string]$Region,
            [Parameter(Mandatory = $true)][string]$ModelName,
            [Parameter(Mandatory = $true)][string]$SkuName
        )

        $catalog = Invoke-Az -AsJson -AllowFailure -AlwaysRun -Arguments @(
            'cognitiveservices', 'model', 'list', '-l', $Region, '-o', 'json'
        )
        if (-not $catalog) { return $null }

        # Not $matches: that is an automatic variable that the -match operator overwrites.
        $offers = @($catalog | Where-Object {
            $_.model.name -eq $ModelName -and ($_.model.skus | Where-Object { $_.name -eq $SkuName })
        })
        if ($offers.Count -eq 0) { return $null }

        # Model versions are date strings, so a descending string sort is a date sort.
        $chosen = $offers | Sort-Object { $_.model.version } -Descending | Select-Object -First 1
        return [pscustomobject]@{
            Name    = $chosen.model.name
            Version = $chosen.model.version
            Format  = $chosen.model.format
        }
    }

    function Get-RemainingQuota {
        param(
            [Parameter(Mandatory = $true)][string]$Region,
            [Parameter(Mandatory = $true)][string]$ModelName,
            [Parameter(Mandatory = $true)][string]$SkuName
        )

        $usage = Invoke-Az -AsJson -AllowFailure -AlwaysRun -Arguments @(
            'cognitiveservices', 'usage', 'list', '-l', $Region, '-o', 'json'
        )
        if (-not $usage) { return $null }

        $quotaName = "OpenAI.$SkuName.$ModelName"
        $entry = $usage | Where-Object { $_.name.value -eq $quotaName } | Select-Object -First 1
        if (-not $entry) { return $null }

        return [int]([math]::Floor($entry.limit - $entry.currentValue))
    }

    function Set-ClipboardSafely {
        param([string]$Text)
        try { Set-Clipboard -Value $Text -ErrorAction Stop; return $true }
        catch { return $false }
    }

    # --------------------------------------------------------------------------------------------
    # Start
    # --------------------------------------------------------------------------------------------

    Write-Host ''
    Write-Host '  Scribe: Microsoft Foundry setup' -ForegroundColor White
    Write-Host '  Creates a Foundry resource, project and model deployment for AI cleanup.' -ForegroundColor DarkGray
    if ($WhatIfPreference) {
        Write-Host '  Running in -WhatIf mode. Nothing will be created.' -ForegroundColor Magenta
    }

    # --------------------------------------------------------------------------------------------
    Write-Step 'Checking the Azure CLI'

    $azCommand = Get-Command az -ErrorAction SilentlyContinue
    if (-not $azCommand) {
        Write-Fail 'The Azure CLI is not installed.'
        Write-Host ''
        Write-Host '  Install it, then close and reopen PowerShell and run this script again:' -ForegroundColor Gray
        Write-Host '    winget install --exact --id Microsoft.AzureCLI' -ForegroundColor White
        Write-Host ''
        Write-Host '  No winget? Download the MSI: https://aka.ms/installazurecliwindows' -ForegroundColor Gray
        Write-Host ''
        return
    }

    $versionInfo = Invoke-Az -AsJson -AlwaysRun -Arguments @('version', '-o', 'json')
    $azVersion = [version]$versionInfo.'azure-cli'
    Write-Detail "Azure CLI $azVersion"

    if ($azVersion -lt $MinimumAzVersion) {
        Write-Fail "Azure CLI $azVersion is too old. Foundry resources need $MinimumAzVersion or newer."
        Write-Host ''
        Write-Host '  Upgrade, then run this script again:' -ForegroundColor Gray
        Write-Host '    az upgrade' -ForegroundColor White
        Write-Host ''
        return
    }
    Write-Good 'Azure CLI is current enough.'

    # --------------------------------------------------------------------------------------------
    Write-Step 'Signing you in to Azure'

    $account = Invoke-Az -AsJson -AllowFailure -AlwaysRun -Arguments @('account', 'show', '-o', 'json')
    if (-not $account) {
        Write-Detail 'No active session. A browser window will open, sign in with your work account.'
        Invoke-Az -AlwaysRun -Arguments @('login', '--only-show-errors', '-o', 'none') | Out-Null
        $account = Invoke-Az -AsJson -AlwaysRun -Arguments @('account', 'show', '-o', 'json')
    }
    Write-Good ("Signed in as {0}" -f $account.user.name)

    $signInAlias = ($account.user.name -split '@')[0] -replace '[^a-zA-Z0-9]', ''
    if (-not $signInAlias) { $signInAlias = 'user' }

    # --------------------------------------------------------------------------------------------
    Write-Step 'Choosing the subscription'

    # --all so subscriptions in other tenants show up too. Personal Visual Studio credit subscriptions
    # very often live in a different tenant from the one az login landed on, and that mismatch is the
    # single most common reason this whole exercise goes sideways.
    $subscriptions = @(Invoke-Az -AsJson -AlwaysRun -Arguments @(
        'account', 'list', '--all', '--query', "[?state=='Enabled']", '-o', 'json'
    ))

    if ($subscriptions.Count -eq 0) {
        Write-Fail 'No enabled Azure subscriptions on this account.'
        Write-Host ''
        Write-Host '  If you are a Microsoft employee you almost certainly have unused Azure credits.' -ForegroundColor Gray
        Write-Host '  Activate them (no credit card, creates a brand new subscription):' -ForegroundColor Gray
        Write-Host '    https://my.visualstudio.com/Benefits' -ForegroundColor White
        Write-Host '  Then run this script again.' -ForegroundColor Gray
        Write-Host ''
        return
    }

    if ($SubscriptionId) {
        $chosenSubscription = $subscriptions | Where-Object { $_.id -eq $SubscriptionId } | Select-Object -First 1
        if (-not $chosenSubscription) {
            throw "Subscription '$SubscriptionId' was not found on this account. Run 'az account list --all -o table' to see what you have."
        }
    }
    else {
        # Visual Studio credit subscriptions float to the top, because that is what this guide is for.
        $ranked = @($subscriptions | Sort-Object @{
            Expression = { if ($_.name -match 'visual studio|vs enterprise|vs professional|msdn') { 0 } else { 1 } }
        }, 'name')

        $chosenSubscription = Read-Choice -Prompt 'Which subscription should hold the Foundry resource?' -Items $ranked -Label {
            param($s)
            $hint = if ($s.name -match 'visual studio|msdn') { '  (Visual Studio credits)' } else { '' }
            "{0}{1}`n         {2}" -f $s.name, $hint, $s.id
        }
    }

    Invoke-Az -AlwaysRun -Arguments @('account', 'set', '--subscription', $chosenSubscription.id) | Out-Null
    Write-Good ("Using {0}" -f $chosenSubscription.name)
    Write-Detail ("Subscription {0}" -f $chosenSubscription.id)
    Write-Detail ("Tenant       {0}" -f $chosenSubscription.tenantId)

    # --------------------------------------------------------------------------------------------
    Write-Step 'Making sure the subscription can create Foundry resources'

    $provider = Invoke-Az -AsJson -AlwaysRun -Arguments @(
        'provider', 'show', '-n', 'Microsoft.CognitiveServices', '--query', '{state:registrationState}', '-o', 'json'
    )
    if ($provider.state -ne 'Registered') {
        Write-Detail 'Registering the Microsoft.CognitiveServices provider. This takes a minute or two.'
        Invoke-Az -Arguments @('provider', 'register', '-n', 'Microsoft.CognitiveServices', '--wait') | Out-Null
        Write-Good 'Provider registered.'
    }
    else {
        Write-Good 'Provider already registered.'
    }

    # --------------------------------------------------------------------------------------------
    Write-Step 'Finding the best region for you'

    if ($Location) {
        $chosenRegion = $Location
        Write-Detail "Region supplied on the command line: $chosenRegion"

        $offer = Get-ModelOffer -Region $chosenRegion -ModelName $Model -SkuName $Sku
        if (-not $offer -and $Sku -eq 'DataZoneStandard') {
            Write-Warn "$Model has no $Sku offer in $chosenRegion. Falling back to GlobalStandard."
            $Sku = 'GlobalStandard'
            $offer = Get-ModelOffer -Region $chosenRegion -ModelName $Model -SkuName $Sku
        }
        if (-not $offer) {
            throw "$Model is not available as $Sku in $chosenRegion. Pick another region, or run without -Location to be shown the ones that work."
        }
        $chosenOffer = $offer
        $remainingQuota = Get-RemainingQuota -Region $chosenRegion -ModelName $Model -SkuName $Sku
    }
    else {
        Write-Detail "Timing $($CandidateRegions.Count) regions from this machine and checking $Model quota in each."
        Write-Detail 'This takes about a minute. Lower milliseconds means less delay on every dictation.'
        Write-Host ''

        $results = New-Object System.Collections.Generic.List[object]
        $index = 0
        foreach ($region in $CandidateRegions) {
            $index++
            Write-Progress -Activity 'Checking regions' -Status $region.Display -PercentComplete (($index / $CandidateRegions.Count) * 100)

            $latency = Measure-RegionLatency -Region $region.Name
            if ($null -eq $latency) { continue }

            $effectiveSku = $Sku
            $offer = Get-ModelOffer -Region $region.Name -ModelName $Model -SkuName $effectiveSku
            if (-not $offer -and $Sku -eq 'DataZoneStandard') {
                $effectiveSku = 'GlobalStandard'
                $offer = Get-ModelOffer -Region $region.Name -ModelName $Model -SkuName $effectiveSku
            }
            if (-not $offer) { continue }

            $quota = Get-RemainingQuota -Region $region.Name -ModelName $Model -SkuName $effectiveSku
            if ($null -ne $quota -and $quota -le 0) { continue }

            $results.Add([pscustomobject]@{
                Name      = $region.Name
                Display   = $region.Display
                LatencyMs = $latency
                Sku       = $effectiveSku
                Quota     = $quota
                Offer     = $offer
            })
        }
        Write-Progress -Activity 'Checking regions' -Completed

        if ($results.Count -eq 0) {
            Write-Fail "Could not find any region offering $Model with quota available on this subscription."
            Write-Host ''
            Write-Host '  Try a different model. Because this script is usually run by piping it into' -ForegroundColor Gray
            Write-Host '  iex, which cannot take options, pass them like this:' -ForegroundColor Gray
            Write-Host "    `$s = irm $ScriptUrl" -ForegroundColor White
            Write-Host '    & ([scriptblock]::Create($s)) -Model gpt-5.4' -ForegroundColor White
            Write-Host ''
            return
        }

        $ordered = @($results | Sort-Object LatencyMs)
        $selection = Read-Choice -Prompt 'Which region?' -Items $ordered -Label {
            param($r)
            $quotaText = if ($null -ne $r.Quota) { "{0}K tokens/min available" -f $r.Quota } else { 'quota unknown' }
            "{0,-28} {1,4} ms   {2,-16} {3}" -f $r.Display, $r.LatencyMs, $r.Sku, $quotaText
        }

        $chosenRegion = $selection.Name
        $chosenOffer = $selection.Offer
        $Sku = $selection.Sku
        $remainingQuota = $selection.Quota
        Write-Good ("Using {0} at {1} ms" -f $selection.Display, $selection.LatencyMs)
    }

    if ($ModelVersion) { $chosenOffer.Version = $ModelVersion }

    if ($null -ne $remainingQuota -and $remainingQuota -lt $Capacity) {
        Write-Warn ("Trimming capacity from {0}K to {1}K tokens/min, which is all the quota you have left here." -f $Capacity, $remainingQuota)
        $Capacity = $remainingQuota
    }

    # --------------------------------------------------------------------------------------------
    Write-Step 'Naming things'

    if (-not $ResourceName) {
        # The resource name becomes a public DNS name, so it has to be globally unique. A short random
        # suffix beats asking a first-time user to guess something nobody else has taken.
        $suffix = -join ((48..57) + (97..122) | Get-Random -Count 5 | ForEach-Object { [char]$_ })
        $stem = if ($signInAlias.Length -gt 12) { $signInAlias.Substring(0, 12) } else { $signInAlias }
        $ResourceName = ("scribe-{0}-{1}" -f $stem, $suffix).ToLowerInvariant()
    }

    Write-Detail "Resource group   $ResourceGroup"
    Write-Detail "Foundry resource $ResourceName"
    Write-Detail "Foundry project  $ProjectName"
    Write-Detail "Model            $($chosenOffer.Name) $($chosenOffer.Version) ($($chosenOffer.Format))"
    Write-Detail "Deployment type  $Sku at ${Capacity}K tokens/min"
    Write-Detail "Region           $chosenRegion"

    if (-not $WhatIfPreference) {
        Write-Host ''
        if (-not (Confirm-Yes 'Create these now?')) {
            Write-Host ''
            Write-Host '  Nothing was created.' -ForegroundColor Gray
            return
        }
    }

    # --------------------------------------------------------------------------------------------
    Write-Step 'Creating the resource group'

    $existingGroup = Invoke-Az -AsJson -AllowFailure -AlwaysRun -Arguments @(
        'group', 'show', '-n', $ResourceGroup, '-o', 'json'
    )
    if ($existingGroup) {
        Write-Good "Resource group $ResourceGroup already exists in $($existingGroup.location)."
    }
    else {
        Invoke-Az -Arguments @('group', 'create', '-n', $ResourceGroup, '-l', $chosenRegion, '-o', 'none') | Out-Null
        Write-Created "Created $ResourceGroup in $chosenRegion."
    }

    # --------------------------------------------------------------------------------------------
    Write-Step 'Creating the Microsoft Foundry resource'

    Write-Detail 'kind=AIServices with project management on. This is what makes it Foundry rather than'
    Write-Detail 'a classic Azure AI services account, which cannot host a project endpoint.'

    # Discarded on purpose: the create call is the point, and binding it to $account would clobber the
    # signed-in account object that earlier steps still describe.
    $null = Invoke-Az -AsJson -Arguments @(
        'cognitiveservices', 'account', 'create',
        '--name', $ResourceName,
        '--resource-group', $ResourceGroup,
        '--location', $chosenRegion,
        '--kind', 'AIServices',
        '--sku', 'S0',
        '--custom-domain', $ResourceName,
        '--allow-project-management',
        '--yes',
        '-o', 'json'
    ) -WhatIfResult ('{"name":"' + $ResourceName + '","properties":{"endpoint":"https://' + $ResourceName + '.cognitiveservices.azure.com/"}}')

    Write-Created "Foundry resource $ResourceName is ready."

    # --------------------------------------------------------------------------------------------
    Write-Step 'Creating the Foundry project'

    $existingProject = Invoke-Az -AsJson -AllowFailure -AlwaysRun -Arguments @(
        'cognitiveservices', 'account', 'project', 'show',
        '--name', $ResourceName, '--resource-group', $ResourceGroup, '--project-name', $ProjectName, '-o', 'json'
    )
    if ($existingProject) {
        Write-Good "Project $ProjectName already exists."
    }
    else {
        Invoke-Az -Arguments @(
            'cognitiveservices', 'account', 'project', 'create',
            '--name', $ResourceName,
            '--resource-group', $ResourceGroup,
            '--project-name', $ProjectName,
            '--location', $chosenRegion,
            '-o', 'none'
        ) | Out-Null
        Write-Created "Created project $ProjectName."
    }

    # --------------------------------------------------------------------------------------------
    Write-Step 'Deploying the model'

    Write-Detail "This is the step that gives you a deployment name, which is what Scribe actually calls."
    $deploymentName = $chosenOffer.Name

    Invoke-Az -Arguments @(
        'cognitiveservices', 'account', 'deployment', 'create',
        '--name', $ResourceName,
        '--resource-group', $ResourceGroup,
        '--deployment-name', $deploymentName,
        '--model-name', $chosenOffer.Name,
        '--model-version', $chosenOffer.Version,
        '--model-format', $chosenOffer.Format,
        '--sku-name', $Sku,
        '--sku-capacity', $Capacity,
        '-o', 'none'
    ) | Out-Null

    Write-Created "Deployed $deploymentName ($Sku, ${Capacity}K tokens/min)."

    # --------------------------------------------------------------------------------------------
    $projectEndpoint = "https://$ResourceName.services.ai.azure.com/api/projects/$ProjectName"
    $servicePrincipal = $null

    if ($SkipServicePrincipal) {
        Write-Step 'Skipping the dedicated identity'
        Write-Detail 'Scribe will use your az login. Set sign-in method to Azure CLI in Scribe.'
    }
    else {
        Write-Step 'Creating a dedicated identity for Scribe'

        Write-Detail 'The Azure CLI has one active account at a time. If you belong to several tenants,'
        Write-Detail 'and most people here do, az login can silently point Scribe at the wrong one.'
        Write-Detail 'A dedicated identity pins Scribe to this tenant permanently.'

        if (-not $ServicePrincipalName) {
            $ServicePrincipalName = "Scribe-AI-Cleanup-$signInAlias"
        }

        $scope = "/subscriptions/$($chosenSubscription.id)/resourceGroups/$ResourceGroup/providers/Microsoft.CognitiveServices/accounts/$ResourceName"

        # Reuse an existing registration rather than creating a duplicate every run. Duplicates are
        # worse than they sound: they all have the same display name, so later on nobody can tell which
        # one Scribe is actually using, and revoking the wrong one breaks cleanup with no obvious cause.
        $existingApp = Invoke-Az -AsJson -AllowFailure -AlwaysRun -Arguments @(
            'ad', 'app', 'list', '--display-name', $ServicePrincipalName, '--query', '[0]', '-o', 'json'
        )

        if ($existingApp) {
            Write-Detail "Reusing the existing registration $ServicePrincipalName."
            Write-Detail 'Issuing a fresh secret, because the old one cannot be read back.'

            $servicePrincipal = Invoke-Az -AsJson -AllowFailure -Arguments @(
                'ad', 'app', 'credential', 'reset',
                '--id', $existingApp.appId,
                '--years', $SecretYears,
                '--display-name', 'scribe-setup',
                '-o', 'json'
            ) -WhatIfResult '{"appId":"00000000-0000-0000-0000-000000000000","password":"<secret>","tenant":"00000000-0000-0000-0000-000000000000"}'

            if ($servicePrincipal -and -not (Test-HasValue $servicePrincipal 'tenant')) {
                # credential reset does not always echo the tenant, but it is the one we are signed in to.
                $servicePrincipal | Add-Member -NotePropertyName tenant -NotePropertyValue $chosenSubscription.tenantId -Force
            }
        }
        else {
            # No --role or --scope here on purpose: that default would grant Contributor across the
            # whole subscription. The role assignment below is scoped to just this one resource.
            $servicePrincipal = Invoke-Az -AsJson -AllowFailure -Arguments @(
                'ad', 'sp', 'create-for-rbac',
                '--name', $ServicePrincipalName,
                '--years', $SecretYears,
                '-o', 'json'
            ) -WhatIfResult '{"appId":"00000000-0000-0000-0000-000000000000","password":"<secret>","tenant":"00000000-0000-0000-0000-000000000000"}'
        }

        if (-not $servicePrincipal) {
            Write-Warn 'Could not create the identity. The usual cause is that your tenant does not let'
            Write-Warn 'you register applications.'
            Write-Host ''
            Write-Host '     Ask your Entra administrator for the Application Developer role, then re-run:' -ForegroundColor Gray
            Write-Host "       `$s = irm $ScriptUrl" -ForegroundColor White
            Write-Host ("       & ([scriptblock]::Create(`$s)) -ResourceName {0} -ResourceGroup {1} -Location {2}" -f $ResourceName, $ResourceGroup, $chosenRegion) -ForegroundColor White
            Write-Host ''
            Write-Warn 'Carrying on without it. Scribe will use your az login instead, which works fine'
            Write-Warn 'as long as az login stays pointed at this tenant.'
        }
        else {
            Write-Created "Identity $ServicePrincipalName is ready."

            # The object ID is not the same as the application (client) ID, and role assignment needs
            # the object ID.
            $objectId = Invoke-Az -AsJson -AllowFailure -Arguments @(
                'ad', 'sp', 'show', '--id', $servicePrincipal.appId, '--query', 'id', '-o', 'json'
            ) -WhatIfResult '"00000000-0000-0000-0000-000000000000"'

            if (-not $objectId) {
                Write-Warn 'The identity exists but has not replicated through Entra yet. Waiting 30 seconds.'
                if (-not $WhatIfPreference) { Start-Sleep -Seconds 30 }
                $objectId = Invoke-Az -AsJson -AllowFailure -Arguments @(
                    'ad', 'sp', 'show', '--id', $servicePrincipal.appId, '--query', 'id', '-o', 'json'
                ) -WhatIfResult '"00000000-0000-0000-0000-000000000000"'
            }

            if ($objectId) {
                # Foundry User, scoped to this one resource. Not the subscription, and deliberately not
                # any of the Cognitive Services roles: Microsoft's Foundry RBAC guidance says those do
                # not apply to Foundry, even though some of them still happen to work today.
                #
                # --assignee-object-id rather than --assignee: the latter does a directory lookup that
                # fails, or worse silently resolves to the wrong object, in tenants where you cannot
                # read all principals.
                $assignment = Invoke-Az -AsJson -AllowFailure -Arguments @(
                    'role', 'assignment', 'create',
                    '--assignee-object-id', $objectId,
                    '--assignee-principal-type', 'ServicePrincipal',
                    '--role', $FoundryUserRoleId,
                    '--scope', $scope,
                    '-o', 'json'
                ) -WhatIfResult '{"id":"whatif"}'

                if ($assignment) {
                    Write-Created 'Granted the Foundry User role on the Foundry resource.'
                    if (-not $WhatIfPreference) {
                        Write-Detail 'Scoped to this one resource, so the identity can call the model and nothing else.'
                        Write-Warn 'Role assignments take up to ten minutes to take effect. A 403 before then is'
                        Write-Warn 'normal and does not mean anything is wrong. Wait rather than changing roles.'
                    }
                }
                else {
                    Write-Warn 'Could not assign the role. You need Owner or User Access Administrator on'
                    Write-Warn 'the resource to do this.'
                    Write-Host ''
                    Write-Host '     Ask whoever administers the subscription to run:' -ForegroundColor Gray
                    Write-Host "       az role assignment create --assignee-object-id $objectId ``" -ForegroundColor White
                    Write-Host "         --assignee-principal-type ServicePrincipal ``" -ForegroundColor White
                    Write-Host "         --role $FoundryUserRoleId ``" -ForegroundColor White
                    Write-Host "         --scope $scope" -ForegroundColor White
                    Write-Host ''
                    Write-Warn 'Until that runs, cleanup will fail with 403 Forbidden.'
                }
            }
        }
    }

    # --------------------------------------------------------------------------------------------
    if ($WhatIfPreference) {
        Write-Host ''
        Write-Host '  ------------------------------------------------------------------' -ForegroundColor DarkGray
        Write-Host '  Dry run finished. Nothing above was created.' -ForegroundColor Magenta
        Write-Host '  ------------------------------------------------------------------' -ForegroundColor DarkGray
        Write-Host ''
        Write-Host '  A real run would give you:' -ForegroundColor Gray
        Write-Host "    Endpoint         $projectEndpoint" -ForegroundColor Gray
        Write-Host "    Deployment name  $deploymentName" -ForegroundColor Gray
        Write-Host ''
        if (-not $SkipServicePrincipal) {
            Write-Host '  The tenant ID, client ID and client secret only exist once the identity is really' -ForegroundColor Gray
            Write-Host '  created, so there is nothing genuine to show for them here.' -ForegroundColor Gray
            Write-Host ''
        }
        Write-Host '  Run the same command without -WhatIf to create it all for real.' -ForegroundColor Gray
        Write-Host ''
        return
    }

    Write-Host ''
    Write-Host '  ------------------------------------------------------------------' -ForegroundColor DarkGray
    Write-Host '  Done. Paste these into Scribe: Settings > AI cleanup > Microsoft Foundry' -ForegroundColor White
    Write-Host '  ------------------------------------------------------------------' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  Endpoint         ' -NoNewline -ForegroundColor Gray
    Write-Host $projectEndpoint -ForegroundColor Green
    Write-Host '  Deployment name  ' -NoNewline -ForegroundColor Gray
    Write-Host $deploymentName -ForegroundColor Green

    if ($servicePrincipal) {
        Write-Host ''
        Write-Host '  Sign-in method   ' -NoNewline -ForegroundColor Gray
        Write-Host 'Service principal' -ForegroundColor Green
        Write-Host '  Tenant ID        ' -NoNewline -ForegroundColor Gray
        Write-Host $servicePrincipal.tenant -ForegroundColor Green
        Write-Host '  Client ID        ' -NoNewline -ForegroundColor Gray
        Write-Host $servicePrincipal.appId -ForegroundColor Green

        # The secret is the one value that cannot be fetched again later, so a missing one is worth
        # saying out loud rather than printing a blank line the user will not notice until cleanup fails.
        if (Test-HasValue $servicePrincipal 'password') {
            Write-Host '  Client secret    ' -NoNewline -ForegroundColor Gray
            Write-Host $servicePrincipal.password -ForegroundColor Green
        }
        else {
            Write-Host '  Client secret    ' -NoNewline -ForegroundColor Gray
            Write-Host 'not returned by Azure' -ForegroundColor Yellow
            Write-Host ''
            Write-Warn ("Azure did not hand back a secret. Issue one with: az ad app credential reset --id {0} --years {1}" -f $servicePrincipal.appId, $SecretYears)
        }
        Write-Host ''
        Write-Host '  That secret is shown once and is a credential. Put it straight into Scribe and do' -ForegroundColor Yellow
        Write-Host '  not paste it into chat, a ticket, or a file. Scribe encrypts it with Windows DPAPI.' -ForegroundColor Yellow
        Write-Host ("  It expires in {0} year(s). Rotate with: az ad app credential reset --id {1}" -f $SecretYears, $servicePrincipal.appId) -ForegroundColor Yellow
        Write-Host ''
        Write-Host '  In Scribe, select Verify service principal before entering the endpoint. If it says' -ForegroundColor Gray
        Write-Host '  invalid client secret, wait a minute and try again: brand new secrets take a moment' -ForegroundColor Gray
        Write-Host '  to propagate and the error reads exactly like a typo.' -ForegroundColor Gray
    }
    else {
        Write-Host ''
        Write-Host '  Sign-in method   ' -NoNewline -ForegroundColor Gray
        Write-Host 'Azure CLI (you are already signed in, nothing else to do)' -ForegroundColor Green
        Write-Host ''
        Write-Host '  Keep in mind: az login has one active account. If you sign in to a different tenant' -ForegroundColor Gray
        Write-Host '  later, cleanup starts failing with AADSTS700016 until you switch back with:' -ForegroundColor Gray
        Write-Host ("    az account set --subscription {0}" -f $chosenSubscription.id) -ForegroundColor DarkGray
    }

    if (Set-ClipboardSafely -Text $projectEndpoint) {
        Write-Host ''
        Write-Detail 'The endpoint is on your clipboard.'
    }

    Write-Host ''
    Write-Host '  Foundry portal: ' -NoNewline -ForegroundColor Gray
    Write-Host "https://ai.azure.com" -ForegroundColor Gray
    Write-Host '  Delete everything later with:' -ForegroundColor Gray
    Write-Host "    az group delete -n $ResourceGroup --yes" -ForegroundColor DarkGray
    Write-Host ''
}
