# Set up AI cleanup with Microsoft Foundry

Scribe transcribes on your own machine and works completely offline. **AI cleanup** is the optional
extra: a language model tidies punctuation, capitalization, spoken self-corrections and repeated
points before the text is typed out. Only the transcribed *text* is ever sent, never audio, and only
to an endpoint you configure.

You can run cleanup fully offline through [Foundry Local](https://learn.microsoft.com/azure/ai-foundry/foundry-local/),
which needs no setup and no Azure account. This guide is for the other option: pointing Scribe at a
frontier model running in **Microsoft Foundry** in the cloud. It is faster and noticeably better at
the hard cases, and if you work at Microsoft it is almost certainly free, because you already have
Azure credits you are not using.

Budget about twenty minutes, most of which is waiting.

## The short version

If you just want it done, skip to [Run it](#run-it). One command does the whole thing. The rest of
this page explains what is happening and what to do when a step misbehaves.

## What you are creating, and why it is free

Four things, which the script creates in order:

| Thing | What it is |
| --- | --- |
| Azure subscription | Where the bill goes. Yours comes with monthly credits. |
| Foundry resource | The account that hosts models. Lives in one datacenter region. |
| Foundry project | A workspace inside that resource. Gives you the endpoint URL Scribe wants. |
| Model deployment | The actual model you call, and the name you call it by. |

Most Visual Studio subscriptions include **monthly Azure credits** that reset every month and expire
if you do not use them. Activating them creates a brand new Azure subscription and does not require
a credit card, so there is no way to accidentally run up a bill against your own money.

Cleanup costs a fraction of a cent per dictation. Heavy daily use lands in the low single-digit
dollars per month, well inside the monthly credit. Nothing is charged while you are not dictating,
because you are billed per token rather than per hour.

> **Check your credits first.** Go to [my.visualstudio.com/Benefits](https://my.visualstudio.com/Benefits)
> and look for the **Azure** tile. If it says Activate, select it. If it says Manage, you already
> have a subscription and can move on. Full detail:
> [Use Azure credits in a Visual Studio subscription](https://learn.microsoft.com/visualstudio/subscriptions/vs-azure-individual).

## Run it

Open **PowerShell** and paste this one line:

```powershell
irm https://raw.githubusercontent.com/ChrisMcKee1/scribe/main/scripts/Setup-ScribeFoundry.ps1 | iex
```

That is the whole thing. You do not need a copy of the Scribe source code, and you do not need to
know any Azure commands. The script signs you in, asks which subscription to use, times the Azure
regions from your own machine to find the fastest one that has capacity, then creates everything and
prints the values to paste into Scribe.

`irm` downloads the script and `iex` runs it. If you would rather read it before running it, open
[the same URL](https://raw.githubusercontent.com/ChrisMcKee1/scribe/main/scripts/Setup-ScribeFoundry.ps1)
in a browser first. That is good practice with any script from the internet, including this one.

### Try it without changing anything

Add `-WhatIf` and it prints every Azure command it would run, creates nothing, and stops.

There is a catch worth knowing: **`irm | iex` cannot pass options to a script.** The pipe hands over
plain text, and there is nowhere to attach `-WhatIf` to it. So for anything with options, download
into a variable and run it as a scriptblock instead:

```powershell
$setup = irm https://raw.githubusercontent.com/ChrisMcKee1/scribe/main/scripts/Setup-ScribeFoundry.ps1
& ([scriptblock]::Create($setup)) -WhatIf
```

### Passing options

Same shape as above. The ones people actually use:

```powershell
$setup = irm https://raw.githubusercontent.com/ChrisMcKee1/scribe/main/scripts/Setup-ScribeFoundry.ps1

# Skip the region test and use a region you already know you want
& ([scriptblock]::Create($setup)) -Location eastus2

# Deploy a different model
& ([scriptblock]::Create($setup)) -Model gpt-5.4

# Use your az login instead of a dedicated identity
& ([scriptblock]::Create($setup)) -SkipServicePrincipal
```

Run `Get-Help` on the downloaded script for the full list.

### If you already have the repo

```powershell
git clone https://github.com/ChrisMcKee1/scribe.git
cd scribe\scripts
.\Setup-ScribeFoundry.ps1 -WhatIf
```

Run it as a normal script and options work the ordinary way, no scriptblock needed.

## Or hand it to an assistant

If you would rather not touch PowerShell at all, open **Scout** or **GitHub Copilot** and paste this:

> Set up Microsoft Foundry for Scribe's AI cleanup on this machine.
>
> Run Scribe's setup script. Do not write your own Azure commands:
>
> ```powershell
> irm https://raw.githubusercontent.com/ChrisMcKee1/scribe/main/scripts/Setup-ScribeFoundry.ps1 | iex
> ```
>
> Install the Azure CLI first if it is missing (`winget install --exact --id Microsoft.AzureCLI`),
> then run the script and follow its prompts. It signs me in, lets me pick the subscription, times
> the Azure regions from this machine to find the fastest one with model quota, creates the Foundry
> resource, project and model deployment, and creates a service principal so Scribe is pinned to one
> tenant.
>
> Important: the script must be the thing that creates the resource, because it uses
> `az cognitiveservices account create --kind AIServices --allow-project-management`, which produces
> a **Microsoft Foundry** resource. Do not substitute the older Azure OpenAI or "Azure AI services"
> commands, and do not create the resource in the portal. Those produce a resource with no project
> endpoint that Scribe cannot use.
>
> When it finishes, show me the Endpoint, Deployment name, Tenant ID, Client ID and Client secret it
> printed so I can paste them into Scribe.

That "do not substitute" paragraph matters more than it looks. Left to their own devices, AI
assistants reliably reach for the older Azure OpenAI or "Azure AI services" commands, because there
is a decade of training data pointing that way. Those produce a resource that looks right, has no
project endpoint, and fails against Scribe in a way that is genuinely hard to diagnose. Telling the
assistant to run *this script* rather than improvise is the single most useful thing on this page.

## What the script does, step by step

Nothing here is hidden, and you can do any of it by hand.

### 1. Installs and checks the Azure CLI

The Azure CLI is the command-line tool that talks to Azure. The script checks the version, because
`--allow-project-management` is the flag that makes a resource a Foundry resource, and older CLI
builds do not have it.

```powershell
winget install --exact --id Microsoft.AzureCLI
```

Close and reopen PowerShell afterwards, otherwise it will not be on your PATH yet.

### 2. Signs you in and picks the subscription

A browser window opens. Sign in with your work account.

If you have several subscriptions, the script lists them and floats Visual Studio credit
subscriptions to the top, since that is what most people want. It shows the tenant for each one, and
it is worth reading that line: **your credit subscription is often in a different tenant from your
day job**, and that mismatch is the root cause of most of the confusing errors later.

### 3. Finds the closest datacenter

Latency to the datacenter is added to every single dictation you clean up, so this is worth thirty
seconds of attention. A region 20 ms away versus one 120 ms away is a difference you can feel.

The script measures it directly from your machine, so there is no third-party website involved and
no guessing:

```
   * [ 1] Central US (Iowa)               54 ms   DataZoneStandard   333K tokens/min available
     [ 2] East US 2 (Virginia)            76 ms   DataZoneStandard   333K tokens/min available
     [ 3] South Central US (Texas)        82 ms   DataZoneStandard   333K tokens/min available
```

It only lists regions that pass all three tests: reachable, offering the model you asked for, and
with quota left on your subscription. Press Enter to take the fastest.

If you would rather use an external reference, [azurespeed.com](https://www.azurespeed.com/Azure/Latency)
does the same thing in a browser. Pass the winner to the script with `-Location eastus2` and it will
skip its own test.

### 4. Creates the Foundry resource

This is the step everything else depends on, and the one that goes wrong when it is done by hand:

```powershell
az cognitiveservices account create `
  --name my-scribe-resource `
  --resource-group rg-scribe-ai `
  --location eastus2 `
  --kind AIServices `
  --sku S0 `
  --custom-domain my-scribe-resource `
  --allow-project-management
```

Three details do the work:

- **`--kind AIServices`** makes it a Foundry resource. The portal's older "Azure OpenAI" and "Azure
  AI services" tiles create a different kind that cannot host a project.
- **`--allow-project-management`** is what lets it contain projects. Without it you get a Foundry-ish
  resource with no project endpoint, which is the confusing middle state most people land in.
- **`--custom-domain`** gives you `https://your-name.services.ai.azure.com` instead of a shared
  regional address. Microsoft Entra sign-in **only works against a custom subdomain**; a regional
  endpoint rejects the token no matter how the permissions are set.

The name becomes a public DNS name, so it has to be globally unique. The script generates one for
you rather than making you guess what is free.

### 5. Creates the project

```powershell
az cognitiveservices account project create `
  --name my-scribe-resource --resource-group rg-scribe-ai `
  --project-name scribe --location eastus2
```

The project is what gives you the endpoint URL Scribe wants:
`https://my-scribe-resource.services.ai.azure.com/api/projects/scribe`

### 6. Deploys the model

The default is `gpt-5.6-terra` on a **DataZoneStandard** deployment. Two reasons: on Scribe's own
[benchmark](model-leaderboard.md) it is the fastest of the top-quality tier, and data zone keeps
your text inside your geography rather than routing it anywhere on the planet with spare capacity.
For dictation that is the right trade.

If the region has no data zone capacity for that model, the script falls back to GlobalStandard and
tells you.

Want a different model? `-Model gpt-5.4` picks the leaderboard's best quality-per-millisecond option.

Capacity defaults to 100K tokens per minute. Because these are pay-per-token deployments, that
number is only a throttling ceiling and does not cost anything by itself.

### 7. Creates a dedicated identity for Scribe

The script creates a **service principal**, which is a login that belongs to Scribe rather than to
you, and grants it access to just this one resource.

This is on by default and it is worth understanding why. The Azure CLI has exactly one active
account at a time. If you sign in to a customer tenant next week, or a colleague's subscription, the
account Scribe was quietly relying on changes underneath it. Cleanup then starts failing with
`AADSTS700016: Application not found in tenant`, weeks after you last thought about Azure, with no
obvious connection to anything you did. A service principal pins Scribe to one identity in one
tenant, permanently.

```powershell
az ad sp create-for-rbac --name "Scribe-AI-Cleanup" --years 1
```

Note there is no `--role` or `--scope` on that command. The default would hand out Contributor
across your whole subscription. Instead the script grants exactly one role, **Foundry User**, scoped
to the single resource:

```powershell
az role assignment create `
  --assignee-object-id $objectId `
  --assignee-principal-type ServicePrincipal `
  --role 53ca6127-db72-4b80-b1b0-d745d6d5456d `
  --scope "/subscriptions/.../accounts/my-scribe-resource"
```

Foundry User is data-plane only: it can call a deployed model and nothing else. It cannot deploy
models, create projects, or assign roles. The role is referenced by ID rather than name on purpose,
because these roles were recently renamed from `Azure AI *` to `Foundry *` and different tools
currently show different labels for the same thing.

**Do not substitute a `Cognitive Services` role here.** Microsoft's
[Foundry RBAC guidance](https://learn.microsoft.com/azure/foundry/concepts/rbac-foundry) says
plainly that role family does not apply to Foundry. Some of them still work today. They are not the
supported path and are not what to build on.

If your tenant does not let you register applications, the script says so, prints exactly what to
ask your administrator for, and carries on without it. You still end up with a working setup using
your `az login`.

Prefer to do this part by hand, or need the portal walkthrough?
See [service-principal-setup.md](service-principal-setup.md).

## Put the values into Scribe

The script finishes by printing everything you need, and puts the endpoint on your clipboard.

Open Scribe, go to **Settings > AI cleanup**, and:

1. Set the provider to **Microsoft Foundry**.
2. Set **Sign-in method** to **Service principal**.
3. Paste the **Directory (tenant) ID**, **Application (client) ID** and **Client secret**.
4. Select **Verify service principal**. Scribe requests a real token, so this tells you the truth
   rather than just checking the format.
5. Paste the **Endpoint** and type the **Deployment name**.
6. Save, then dictate something into Notepad to confirm.

> **If Verify fails immediately with "invalid client secret", wait a minute and try again.** A
> brand new secret takes up to a couple of minutes to propagate through Entra, and until it does the
> error reads exactly like you mistyped it. You probably did not.

The secret is encrypted on your PC with Windows DPAPI, scoped to your user account, so no other user
on the machine can read it. Scribe never writes it to an environment variable or a file on disk.

Put the expiry date in your calendar. The default secret lasts a year, and when it lapses cleanup
starts failing for a reason nobody remembers. Rotate it with:

```powershell
az ad app credential reset --id <client-id> --years 1
```

## When something goes wrong

Scribe reports the HTTP status behind any cleanup failure, and that status is the fastest way to
narrow things down. A **403 means your credentials were accepted and access was refused**, so
nothing about the secret or the tenant is the problem. A **404 means access was fine and the
deployment name was not found**. Read the status before changing anything.

| What you see | What it means | What to do |
| --- | --- | --- |
| Script says the CLI is too old | `--allow-project-management` is missing | Run `az upgrade`, then re-run the script |
| No subscriptions listed | Credits not activated yet | [my.visualstudio.com/Benefits](https://my.visualstudio.com/Benefits), activate Azure, re-run |
| No regions offered the model | Region has no quota for it on your subscription | Try `-Model gpt-5.4` |
| Cannot register applications | Your tenant restricts app registration | Ask your admin for the **Application Developer** role |
| `AADSTS700016` | The app registration is in a different tenant than the one entered | Check the tenant ID matches the directory that owns the registration |
| `AADSTS7000215` invalid secret | Usually a brand new secret that has not propagated | Wait a minute, verify again. Then check you copied the secret Value, not the Secret ID |
| 401 Unauthorized | The token was rejected | Confirm the resource has a custom subdomain (`services.ai.azure.com`, not a regional address) |
| 403 right after setup | Role assignment has not propagated | Wait. Ten minutes is normal. Do not start swapping roles |
| 403 an hour later | Role missing, wrong resource, or a look-alike role | Re-check the assignment and confirm it is **Foundry User** on the resource hosting the deployment |
| 404 Not found | Credentials fine, deployment name wrong | Check the deployment name against that specific resource |
| 429 Too many requests | Over quota | Wait, or raise the deployment's capacity |

More depth on the authentication side, including every role that sounds correct and is not, is in
[service-principal-setup.md](service-principal-setup.md).

## Removing it

Everything lives in one resource group, so there is one command:

```powershell
az group delete -n rg-scribe-ai --yes
```

The service principal is separate, since it lives in Entra rather than in the subscription:

```powershell
az ad app delete --id <client-id>
```

Turning AI cleanup off in Scribe's settings, or from the tray menu, stops all of it immediately.
Dictation carries on working offline exactly as before.
