<#
.SYNOPSIS
    Sets up a Microsoft Foundry resource, companion project and chat model deployment for Scribe's AI cleanup.

.DESCRIPTION
    Run it with no arguments and answer the prompts. It signs you in, lets you pick the Azure
    subscription (your Visual Studio credits work fine), measures which regions are actually fast
    from where you are sitting, checks the model quota in those regions, then creates:

      - a resource group
      - a Microsoft Foundry resource (kind AIServices, project management enabled, custom subdomain)
      - a companion Foundry project for the portal and Entra endpoint shape
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
    look similar in the portal and are not interchangeable: the Foundry shape gives you the
    supported role model and the optional project endpoint used by the portal.

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
    Model to deploy. When omitted, tries gpt-5.6-terra, then gpt-5-mini, then gpt-5-nano, and takes
    the first with quota available. Naming one explicitly disables the fallback.

.PARAMETER ModelVersion
    Pin a specific model version. Defaults to the newest version the region offers.

.PARAMETER Sku
    Deployment type. Defaults to DataZoneStandard, falling back to GlobalStandard if the region
    has no data zone quota for the model.

.PARAMETER Capacity
    Rate limit in thousands of tokens per minute. Defaults to 100, trimmed to whatever quota you
    actually have left. Standard deployments bill per token, so a larger number costs nothing
    extra, it just raises the ceiling before you get throttled.

.PARAMETER UseServicePrincipal
    Create a dedicated Entra identity instead of using an API key. Needs permission to register an
    application in your tenant. Worth choosing when policy forbids key authentication, or when you
    want the access scoped by a role rather than by a key.

.PARAMETER ServicePrincipalName
    Name for the Entra app registration. Defaults to Scribe-AI-Cleanup-<your alias>. Reused if it
    already exists, so re-running the script does not litter your tenant with duplicates.
    Only used with -UseServicePrincipal.

.PARAMETER SecretYears
    How long the client secret lasts before it expires. Defaults to 1. Microsoft recommends under
    2, and the maximum Entra allows is 2. Only used with -UseServicePrincipal.

.PARAMETER NonInteractive
    Never prompt. Any answer that was not supplied as a parameter stops the run with a message
    naming the parameter to pass. Detected automatically when there is no console to prompt on, so
    an AI assistant or a CI job does not have to set it.

.PARAMETER Yes
    Skip the confirmation before anything is created.

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
    & ([scriptblock]::Create($s)) -Location eastus2

    Piping into iex cannot pass parameters, so build a scriptblock when you need them.

.EXAMPLE
    .\Setup-ScribeFoundry.ps1 -SubscriptionId <id> -Location eastus2 -Yes

    Unattended. Everything answered up front, so nothing prompts.

.EXAMPLE
    .\Setup-ScribeFoundry.ps1 -UseServicePrincipal

    Use a dedicated Entra identity instead of an API key.

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
    [string]$Model,
    [string]$ModelVersion,
    [ValidateSet('DataZoneStandard', 'GlobalStandard', 'Standard')]
    [string]$Sku = 'DataZoneStandard',
    [ValidateRange(1, 5000)]
    [int]$Capacity = 100,
    [switch]$UseServicePrincipal,
    [string]$ServicePrincipalName,
    [ValidateRange(1, 2)]
    [int]$SecretYears = 1,
    [switch]$NonInteractive,
    [switch]$Yes
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

    # Tried in order until one has quota in a reachable region. Ordered by cleanup quality, not by
    # quota: measured remaining quota decides the winner, because assuming a smaller model has more
    # headroom is wrong. At the entry quota tier gpt-5-mini carries the SAME global standard limit as
    # gpt-5.6-terra, and only gpt-5-nano is meaningfully higher.
    $ModelFallbackChain = @('gpt-5.6-terra', 'gpt-5-mini', 'gpt-5-nano')

    # An AI agent or a CI job has no console to answer a prompt. Blocking on Read-Host there looks
    # like a hang, which is exactly how this script stranded its first agent-driven run.
    $Interactive = -not $NonInteractive -and
                   -not [System.Console]::IsInputRedirected -and
                   [Environment]::UserInteractive

    # Raised whenever a prompt is unavoidable but nobody can answer it, so the caller gets an
    # actionable message naming the parameter to pass instead of an unexplained stall.
    function Stop-NeedsInput {
        param([string]$What, [string]$Parameter)
        Write-Fail "This step needs an answer for $What, but nothing can respond."
        Write-Host ''
        Write-Host '  Re-run with the value supplied up front:' -ForegroundColor Gray
        Write-Host "    $Parameter" -ForegroundColor White
        Write-Host ''
        Write-Host '  Every prompt in this script has a matching parameter, so it can run start to' -ForegroundColor Gray
        Write-Host '  finish unattended. Run Get-Help against the script for the full list.' -ForegroundColor Gray
        Write-Host ''
        throw "Input required for $What in a non-interactive session."
    }

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
            [int]$DefaultIndex = 0,
            [string]$NonInteractiveParameter
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

        # Listing the options above before bailing is deliberate: an agent that cannot answer can at
        # least show the human what the choices were, instead of failing with nothing to act on.
        if (-not $Interactive) {
            if ($NonInteractiveParameter) {
                Stop-NeedsInput -What $Prompt -Parameter $NonInteractiveParameter
            }
            Write-Detail 'Not interactive, taking the marked default.'
            return $Items[$DefaultIndex]
        }

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
        if ($Yes) { return $true }
        if (-not $Interactive) {
            # Creating billable Azure resources is not something to assume consent for. An automated
            # caller says so explicitly with -Yes; anything else stops here rather than provisioning
            # on someone's subscription because nobody was present to object.
            Stop-NeedsInput -What $Prompt -Parameter '-Yes'
        }
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
    Write-Host '  Creates a Foundry resource, companion project and model deployment for AI cleanup.' -ForegroundColor DarkGray
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
        # az login opens a browser and blocks. With nobody to complete it that reads as a hang, which
        # is exactly how this script stranded its first agent-driven run, so say what to do instead.
        if (-not $Interactive) {
            Stop-NeedsInput -What 'an Azure sign-in (run az login first)' -Parameter 'az login'
        }

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
            # Naming BOTH sides matters. The common case is someone whose Azure credits live on a
            # personal account while az login landed on their work account, and a message that only
            # says "not found" sends them hunting for a typo in the subscription id instead.
            Write-Fail "Subscription $SubscriptionId is not available to the signed-in account."
            Write-Host ''
            Write-Host ("  Signed in as:  {0}" -f $account.user.name) -ForegroundColor White
            Write-Host ("  Looking for:   {0}" -f $SubscriptionId) -ForegroundColor White
            Write-Host ''
            Write-Host '  That subscription almost certainly belongs to a different account. Azure credits' -ForegroundColor Gray
            Write-Host '  from a personal Visual Studio subscription often sit on a personal account while' -ForegroundColor Gray
            Write-Host '  az login lands on a work account, or the other way round.' -ForegroundColor Gray
            Write-Host ''
            Write-Host '  Subscriptions this account CAN see:' -ForegroundColor Gray
            foreach ($s in $subscriptions) {
                Write-Host ("    {0}  {1}" -f $s.id, $s.name) -ForegroundColor DarkGray
            }
            Write-Host ''
            Write-Host '  To switch accounts:' -ForegroundColor Gray
            Write-Host '    az logout' -ForegroundColor White
            Write-Host '    az login' -ForegroundColor White
            Write-Host ''
            throw "Subscription $SubscriptionId is not visible to $($account.user.name)."
        }
    }
    else {
        # Visual Studio credit subscriptions float to the top, because that is what this guide is for.
        $ranked = @($subscriptions | Sort-Object @{
            Expression = { if ($_.name -match 'visual studio|vs enterprise|vs professional|msdn') { 0 } else { 1 } }
        }, 'name')

        $chosenSubscription = Read-Choice -Prompt 'Which subscription should hold the Foundry resource?' -Items $ranked -NonInteractiveParameter '-SubscriptionId <id>' -Label {
            param($s)
            $hint = if ($s.name -match 'visual studio|msdn') { '  (Visual Studio credits)' } else { '' }
            "{0}{1}`n         {2}" -f $s.name, $hint, $s.id
        }
    }

    Invoke-Az -AlwaysRun -Arguments @('account', 'set', '--subscription', $chosenSubscription.id) | Out-Null
    Write-Good ("Using {0}" -f $chosenSubscription.name)
    Write-Detail ("Subscription {0}" -f $chosenSubscription.id)
    Write-Detail ("Tenant       {0}" -f $chosenSubscription.tenantId)

    # Say this now rather than letting the user discover it when a deployment is refused. Credit
    # subscriptions get real but modest quota, which is plenty for dictation, and quota increase
    # requests on them are usually turned down because there is no enterprise agreement behind them.
    $isCreditSubscription = $chosenSubscription.name -match 'visual studio|vs enterprise|vs professional|msdn|dev/test|azure pass'
    if ($isCreditSubscription) {
        Write-Host ''
        Write-Detail 'This looks like a Visual Studio credit subscription. That is the ideal setup for'
        Write-Detail 'Scribe: cleanup costs a fraction of a cent per dictation, and Azure stops rather'
        Write-Detail 'than bills you if the monthly credit ever ran out.'
        Write-Detail 'Model quota on these is modest but far more than Scribe needs.'
    }

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
    Write-Step 'Choosing a model and region'

    # An explicit -Model means the caller knows what they want, so honour it and do not silently
    # substitute something else. Otherwise walk the chain until one has quota somewhere reachable.
    $modelsToTry = if ($Model) { @($Model) } else { $ModelFallbackChain }

    if ($Location) {
        $chosenRegion = $Location
        Write-Detail "Region supplied on the command line: $chosenRegion"

        $chosenOffer = $null
        foreach ($candidate in $modelsToTry) {
            foreach ($trySku in @($Sku, 'GlobalStandard' | Select-Object -Unique)) {
                $offer = Get-ModelOffer -Region $chosenRegion -ModelName $candidate -SkuName $trySku
                if (-not $offer) { continue }
                $quota = Get-RemainingQuota -Region $chosenRegion -ModelName $candidate -SkuName $trySku
                if ($null -ne $quota -and $quota -le 0) { continue }
                $chosenOffer = $offer
                $Sku = $trySku
                $Model = $candidate
                $remainingQuota = $quota
                break
            }
            if ($chosenOffer) { break }
        }

        if (-not $chosenOffer) {
            Write-Fail "No model from the list is available with quota in $chosenRegion."
            Write-Host ''
            Write-Host ("  Tried: {0}" -f ($modelsToTry -join ', ')) -ForegroundColor Gray
            Write-Host '  Run without -Location to be shown every region that does work.' -ForegroundColor Gray
            Write-Host ''
            return
        }
        Write-Good ("Using {0} in {1}." -f $chosenOffer.Name, $chosenRegion)
    }
    else {
        # Latency is measured once and reused across every model, because the TCP probe is the slow
        # part and the answer does not change per model.
        Write-Detail "Timing $($CandidateRegions.Count) regions from this machine."
        Write-Detail 'This takes about a minute. Lower milliseconds means less delay on every dictation.'

        $reachable = New-Object System.Collections.Generic.List[object]
        $index = 0
        foreach ($region in $CandidateRegions) {
            $index++
            Write-Progress -Activity 'Timing regions' -Status $region.Display -PercentComplete (($index / $CandidateRegions.Count) * 100)
            $latency = Measure-RegionLatency -Region $region.Name
            if ($null -eq $latency) { continue }
            $reachable.Add([pscustomobject]@{ Name = $region.Name; Display = $region.Display; LatencyMs = $latency })
        }
        Write-Progress -Activity 'Timing regions' -Completed

        if ($reachable.Count -eq 0) {
            Write-Fail 'Could not reach any Azure region from this machine.'
            Write-Host ''
            Write-Host '  Check your network connection, then run this again.' -ForegroundColor Gray
            Write-Host ''
            return
        }

        $results = @()
        $triedModels = @()
        foreach ($candidate in $modelsToTry) {
            $triedModels += $candidate
            Write-Host ''
            Write-Detail "Checking $candidate quota across $($reachable.Count) reachable regions."

            $found = New-Object System.Collections.Generic.List[object]
            foreach ($region in ($reachable | Sort-Object LatencyMs)) {
                $effectiveSku = $Sku
                $offer = Get-ModelOffer -Region $region.Name -ModelName $candidate -SkuName $effectiveSku
                if (-not $offer -and $Sku -eq 'DataZoneStandard') {
                    $effectiveSku = 'GlobalStandard'
                    $offer = Get-ModelOffer -Region $region.Name -ModelName $candidate -SkuName $effectiveSku
                }
                if (-not $offer) { continue }

                $quota = Get-RemainingQuota -Region $region.Name -ModelName $candidate -SkuName $effectiveSku
                if ($null -ne $quota -and $quota -le 0) { continue }

                $found.Add([pscustomobject]@{
                    Name      = $region.Name
                    Display   = $region.Display
                    LatencyMs = $region.LatencyMs
                    Sku       = $effectiveSku
                    Quota     = $quota
                    Offer     = $offer
                })
            }

            if ($found.Count -gt 0) {
                $results = @($found)
                $Model = $candidate
                if ($triedModels.Count -gt 1) {
                    Write-Warn ("No quota for {0}, falling back to {1}." -f (($triedModels | Select-Object -SkipLast 1) -join ', '), $candidate)
                }
                break
            }
        }

        if ($results.Count -eq 0) {
            Write-Fail 'No model in the list has quota available on this subscription.'
            Write-Host ''
            Write-Host ("  Tried: {0}" -f ($triedModels -join ', ')) -ForegroundColor Gray
            Write-Host ''
            Write-Host '  Request more quota at https://aka.ms/oai/stuquotarequest' -ForegroundColor Gray
            if ($isCreditSubscription) {
                Write-Host '  Note: increases on Visual Studio credit subscriptions are frequently declined,' -ForegroundColor Gray
                Write-Host '  because approval favours accounts with an enterprise agreement. Foundry Local' -ForegroundColor Gray
                Write-Host '  runs cleanup entirely on this machine with no quota and no account at all.' -ForegroundColor Gray
            }
            Write-Host ''
            Write-Host '  To try a specific model instead:' -ForegroundColor Gray
            Write-Host "    `$s = irm $ScriptUrl" -ForegroundColor White
            Write-Host '    & ([scriptblock]::Create($s)) -Model gpt-5-nano' -ForegroundColor White
            Write-Host ''
            return
        }

        $ordered = @($results | Sort-Object LatencyMs)
        $selection = Read-Choice -Prompt 'Which region?' -Items $ordered -NonInteractiveParameter '-Location <region>' -Label {
            param($r)
            $quotaText = if ($null -ne $r.Quota) { "{0}K tokens/min available" -f $r.Quota } else { 'quota unknown' }
            "{0,-28} {1,4} ms   {2,-16} {3}" -f $r.Display, $r.LatencyMs, $r.Sku, $quotaText
        }

        $chosenRegion = $selection.Name
        $chosenOffer = $selection.Offer
        $Sku = $selection.Sku
        $remainingQuota = $selection.Quota
        Write-Good ("Using {0} in {1} at {2} ms" -f $chosenOffer.Name, $selection.Display, $selection.LatencyMs)
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
    Write-Detail 'a classic Azure AI services account. The project is useful, but cleanup calls the account.'

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
    Write-Step 'Creating the companion Foundry project'

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

    Write-Detail 'Scribe can call the account endpoint without a project. The project costs nothing and'
    Write-Detail 'keeps the setup aligned with the Foundry portal and Entra endpoint shape.'

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
    $apiKey = $null

    if (-not $UseServicePrincipal) {
        # API key is the default because it is the shortest path to a working setup and needs no
        # directory permissions at all. Creating an app registration requires rights that plenty of
        # corporate tenants withhold, and on a personal credit subscription it is pure ceremony: the
        # key is scoped to this one resource either way.
        Write-Step 'Reading the API key'

        Write-Detail 'Scribe will authenticate with a key belonging to this resource. No app'
        Write-Detail 'registration, no tenant permissions, and nothing to expire in a year.'

        $keys = Invoke-Az -AsJson -AllowFailure -Arguments @(
            'cognitiveservices', 'account', 'keys', 'list',
            '--name', $ResourceName, '--resource-group', $ResourceGroup, '-o', 'json'
        ) -WhatIfResult '{"key1":"<key>","key2":"<key>"}'

        if ($keys -and (Test-HasValue $keys 'key1')) {
            $apiKey = $keys.key1
            Write-Created 'Got the key.'
        }
        else {
            Write-Warn 'Could not read the resource keys. You can copy one later from the Azure portal,'
            Write-Warn 'under the resource, Resource Management, Keys and Endpoint.'
        }
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
            Write-Host '     Ask your Entra administrator for the Application Developer role, then re-run' -ForegroundColor Gray
            Write-Host '     with -UseServicePrincipal to try again:' -ForegroundColor Gray
            Write-Host "       `$s = irm $ScriptUrl" -ForegroundColor White
            Write-Host ("       & ([scriptblock]::Create(`$s)) -ResourceName {0} -ResourceGroup {1} -Location {2} -UseServicePrincipal" -f $ResourceName, $ResourceGroup, $chosenRegion) -ForegroundColor White
            Write-Host ''
            # Falling through to the API key keeps the run useful instead of abandoning a resource
            # that is already built. Reading the key here matters: the summary below reports whatever
            # credential actually exists, so a failed identity can never print as a finished setup.
            Write-Warn 'Falling back to an API key so this run still leaves you with a working setup.'
            $keys = Invoke-Az -AsJson -AllowFailure -Arguments @(
                'cognitiveservices', 'account', 'keys', 'list',
                '--name', $ResourceName, '--resource-group', $ResourceGroup, '-o', 'json'
            ) -WhatIfResult '{"key1":"<key>","key2":"<key>"}'
            if ($keys -and (Test-HasValue $keys 'key1')) {
                $apiKey = $keys.key1
                Write-Created 'Got the key.'
            }
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
        Write-Host ("    Endpoint         {0}" -f $(if ($UseServicePrincipal) { $projectEndpoint } else { "https://$ResourceName.services.ai.azure.com/" })) -ForegroundColor Gray
        Write-Host "    Deployment name  $deploymentName" -ForegroundColor Gray
        Write-Host ''
        if ($UseServicePrincipal) {
            Write-Host '  The tenant ID, client ID and client secret only exist once the identity is really' -ForegroundColor Gray
            Write-Host '  created, so there is nothing genuine to show for them here.' -ForegroundColor Gray
            Write-Host ''
        }
        else {
            Write-Host '  The API key only exists once the resource is really created, so there is nothing' -ForegroundColor Gray
            Write-Host '  genuine to show for it here.' -ForegroundColor Gray
            Write-Host ''
        }
        Write-Host '  Run the same command without -WhatIf to create it all for real.' -ForegroundColor Gray
        Write-Host ''
        return
    }

    # A key authenticates against the resource rather than the project, and Scribe rewrites a project
    # URL down to the account host when a key is set. Showing the address that will actually be used
    # avoids handing someone a URL that silently is not the one in play.
    $accountEndpoint = "https://$ResourceName.services.ai.azure.com/"
    $endpointForScribe = if ($servicePrincipal) { $projectEndpoint } else { $accountEndpoint }

    Write-Host ''
    Write-Host '  ------------------------------------------------------------------' -ForegroundColor DarkGray
    Write-Host '  Done. Paste these into Scribe: Settings > AI cleanup > Microsoft Foundry' -ForegroundColor White
    Write-Host '  ------------------------------------------------------------------' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  Endpoint         ' -NoNewline -ForegroundColor Gray
    Write-Host $endpointForScribe -ForegroundColor Green
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
        Write-Host 'API key' -ForegroundColor Green
        if ($apiKey) {
            Write-Host '  API key          ' -NoNewline -ForegroundColor Gray
            Write-Host $apiKey -ForegroundColor Green
            Write-Host ''
            Write-Host '  That key is a credential. Put it straight into Scribe and do not paste it into' -ForegroundColor Yellow
            Write-Host '  chat, a ticket, or a file. Scribe encrypts it with Windows DPAPI.' -ForegroundColor Yellow
            Write-Host '  It does not expire. Rotate it any time with:' -ForegroundColor Yellow
            Write-Host ("    az cognitiveservices account keys regenerate -n {0} -g {1} --key-name key1" -f $ResourceName, $ResourceGroup) -ForegroundColor DarkGray
        }
        else {
            Write-Host '  API key          ' -NoNewline -ForegroundColor Gray
            Write-Host 'could not be read, copy it from the portal' -ForegroundColor Yellow
            Write-Host ''
            Write-Host ("    az cognitiveservices account keys list -n {0} -g {1}" -f $ResourceName, $ResourceGroup) -ForegroundColor DarkGray
        }
        Write-Host ''
        Write-Host '  Note on the endpoint: a key authenticates against the resource rather than the' -ForegroundColor Gray
        Write-Host '  project, so the account address above is the one to use. The deployment is' -ForegroundColor Gray
        Write-Host '  account-hosted, so the project is not required for cleanup.' -ForegroundColor Gray
        Write-Host ''
        Write-Host '  Prefer a dedicated identity instead? Re-run with -UseServicePrincipal. It needs' -ForegroundColor Gray
        Write-Host '  permission to register an app in your tenant, which not every tenant grants.' -ForegroundColor Gray
    }

    if (Set-ClipboardSafely -Text $endpointForScribe) {
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
