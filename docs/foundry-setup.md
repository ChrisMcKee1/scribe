# Set up AI cleanup with Microsoft Foundry

Scribe transcribes on your own machine and works completely offline. **AI cleanup** is the optional
extra: a language model tidies punctuation, capitalization, spoken self-corrections and repeated
points before the text is typed out. Only the transcribed *text* is ever sent, never audio, and only
to an endpoint you configure.

This guide covers pointing Scribe at a frontier model running in **Microsoft Foundry** in the cloud,
which is faster and noticeably better at the hard cases than running locally. For most people it
costs nothing, because they already have Azure credits they are not using.

## Start here: you probably already have Azure credits

Most people reading this can get cloud cleanup for free, and a lot of them already can and do not
know it. **A Visual Studio subscription includes monthly Azure credits**, and those credits are far
more than Scribe will ever use.

**If you work at Microsoft, assume you have this.** Every full time employee gets a Visual Studio
Enterprise subscription. You almost certainly have $150 a month of Azure credit sitting unused right
now. Go straight to [activation](#activate-your-credits).

If you do not work at Microsoft, you may still have one through your employer, an MSDN
subscription, or a partner benefit. Here is what each level includes:

| Visual Studio subscription | Monthly Azure credit |
| --- | --- |
| Enterprise (annual) | **$150** |
| MSDN Platforms | **$100** |
| Professional (annual) | **$50** |
| Test Professional | **$50** |

The credits renew every month and expire if you do not use them, so there is nothing to save up and
nothing to lose by turning this on.

### Why this is safe

This is the part that stops people, so it is worth being explicit:

- **No credit card is required.** You are not putting a payment method on file.
- **Azure stops rather than bills you.** When the monthly credit runs out, usage halts. There is no
  overage, no surprise invoice, and no way to accidentally spend your own money.
- **Scribe will not come close to the cap.** Cleanup costs a fraction of a cent per dictation. Heavy
  daily use lands in the low single digit dollars per month, and you are billed per token, so
  nothing accrues while you are not dictating.

### Activate your credits

Go to [my.visualstudio.com/benefits](https://my.visualstudio.com/benefits) and find the **Azure**
tile.

- If it says **Activate**, select it. This creates a brand new Azure subscription linked to your
  account, which is where everything below will live.
- If it says **Manage**, you already have one. Move on.

Background reading, if you want it:
[Use Azure credits in a Visual Studio subscription](https://learn.microsoft.com/visualstudio/subscriptions/vs-azure-individual).

### No subscription at all?

You can still use Scribe's AI cleanup with no Azure account whatsoever. Run it fully offline through
[Foundry Local](https://learn.microsoft.com/azure/ai-foundry/foundry-local/), which needs no setup,
no sign in and no credits. It is slower and less capable on hard sentences than a frontier cloud
model, but it is completely private and completely free. The rest of this page is only for people
who want the cloud option.

### One expectation to set about quota

Credit subscriptions get real but modest model quota, which is plenty for dictation cleanup. If you
later want a much larger allowance, be aware that quota increase requests are usually approved for
customers with an enterprise agreement, and a personal credit subscription generally is not one.
Microsoft does not state this as a hard rule, so it is worth knowing rather than worth fighting. In
practice the default quota is far more than Scribe needs.

## What you are creating

Three things, which the script creates in order inside the subscription above:

| Thing | What it is |
| --- | --- |
| Foundry resource | The account that hosts models. Lives in one datacenter region. Required. |
| Foundry project | A workspace inside that resource. Useful in the Foundry portal, but not required for Scribe's cleanup call. |
| Model deployment | The actual model you call, and the name you call it by. Required. |

Budget about twenty minutes, most of which is waiting. If you just want it done, skip to
[Run it](#run-it); the rest of this page explains what is happening and what to do when a step
misbehaves.

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

# Pin one model instead of letting it fall back through the list
& ([scriptblock]::Create($setup)) -Model gpt-5-mini

# Use a dedicated Entra identity instead of an API key
& ([scriptblock]::Create($setup)) -UseServicePrincipal

# Run start to finish with no prompts, for a script or an AI assistant
& ([scriptblock]::Create($setup)) -SubscriptionId <id> -Location eastus2 -Yes
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
> Run Scribe's setup script. Do not write your own Azure commands. Save it to a file and run it,
> rather than piping it into iex, so Defender sees a normal script rather than a download cradle:
>
> ```powershell
> $f = "$env:TEMP\Setup-ScribeFoundry.ps1"
> irm https://raw.githubusercontent.com/ChrisMcKee1/scribe/main/scripts/Setup-ScribeFoundry.ps1 -OutFile $f
> & $f -SubscriptionId <my subscription id> -Yes
> ```
>
> Install the Azure CLI first if it is missing (`winget install --exact --id Microsoft.AzureCLI`).
> Run `az login` and then `az account list --all -o table` and show me the list so I can tell you
> which subscription to use, because you cannot answer its prompts yourself. If I have a Visual
> Studio subscription, that is the one to pick.
>
> The script signs me in, finds the fastest Azure region with model quota, creates the Foundry
> resource, companion project and model deployment, and reads the API key Scribe needs. Every prompt has a
> matching parameter, so pass values rather than trying to answer interactively. If it stops asking
> for something, it will name the parameter to use.
>
> Important: the script must be the thing that creates the resource, because it uses
> `az cognitiveservices account create --kind AIServices --allow-project-management`, which produces
> a **Microsoft Foundry** resource. Do not substitute the older Azure OpenAI or "Azure AI services"
> commands, and do not create the resource in the portal. Those produce a resource that fails
> against Scribe in a way that is genuinely hard to diagnose.
>
> When it finishes, show me the Endpoint, Deployment name and API key it printed so I can paste them
> into Scribe.

Two things in that prompt matter more than they look.

The **"do not substitute"** paragraph: left to their own devices, AI assistants reliably reach for
the older Azure OpenAI or "Azure AI services" commands, because there is a decade of training data
pointing that way. Those produce a resource that looks right and fails in a confusing way.

The **subscription id**: an assistant runs without a console, so it cannot answer the script's
prompts. The script detects this and stops with a clear message naming the parameter it needs rather
than hanging, but you will get there faster by telling it the subscription up front. This matters
most if you have credits on a personal account and a work account signed in, or the reverse, since
the two accounts see different subscriptions.

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

This is the account that hosts the model deployment. It is the part Scribe truly needs, and the one
that goes wrong when it is done by hand:

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
- **`--allow-project-management`** is what lets it contain projects. Scribe can call the account
  endpoint without a project, but the project keeps the setup aligned with the Foundry portal and
  costs nothing.
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

The project gives you the endpoint URL shown on the Foundry project page:
`https://my-scribe-resource.services.ai.azure.com/api/projects/scribe`

For Scribe's single-turn cleanup call, the project is not load-bearing. Empirical testing confirmed
that an account with a deployed model can serve `https://my-resource.services.ai.azure.com/openai/v1/`
even before any project exists. The script still creates the project because it is free, it matches
the portal path most people see, and it remains the recommended endpoint shape when you use Entra
authentication instead of an API key.

### 6. Deploys the model

The first choice is `gpt-5.6-terra` on a **DataZoneStandard** deployment. Two reasons: on Scribe's
own [benchmark](model-leaderboard.md) it is the fastest of the top-quality tier, and data zone keeps
your text inside your geography rather than routing it anywhere on the planet with spare capacity.
For dictation that is the right trade.

The script does not assume you have quota for it. It tries each of these in turn and takes the first
one your subscription can actually deploy:

1. `gpt-5.6-terra`
2. `gpt-5-mini`
3. `gpt-5-nano`

It tells you when it falls back, so you always know which model you ended up with. If the region has
no data zone capacity, it falls back to GlobalStandard and tells you that too.

Worth knowing, because it is counterintuitive: a smaller model does not automatically mean more
quota. `gpt-5-mini` starts with the same global standard allowance as `gpt-5.6-terra`, and only
`gpt-5-nano` is meaningfully higher. That is exactly why the script measures your real remaining
quota rather than guessing from model size.

Want to pin one? `-Model gpt-5-mini` skips the fallback and deploys only that.

Capacity defaults to 100K tokens per minute. Because these are pay-per-token deployments, that
number is only a throttling ceiling and does not cost anything by itself.

### 7. Reads the API key

The script finishes by reading an **API key** belonging to the resource it just created. That key is
what Scribe uses to authenticate.

```powershell
az cognitiveservices account keys list --name my-scribe-resource --resource-group rg-scribe-ai
```

This is the default because it is the shortest path to something that works. It needs no app
registration, no directory permissions, and nothing expires in a year. The key is scoped to this one
resource, so it can call your model and nothing else in your subscription.

> **On the endpoint.** A key authenticates against the *resource* rather than the project, so the
> script gives you the account address (`https://your-name.services.ai.azure.com/`) rather than the
> project URL. Nothing is lost for cleanup: the deployment is account-hosted. Scribe does the same
> rewrite internally if you paste a project URL with a key, which is why the two look different.

Keep the key private. Scribe encrypts it on your PC with Windows DPAPI, scoped to your user account.
Rotate it any time:

```powershell
az cognitiveservices account keys regenerate --name my-scribe-resource --resource-group rg-scribe-ai --key-name key1
```

### 7b. Or a dedicated identity, if you prefer

Run the script with `-UseServicePrincipal` and it creates a **service principal** instead: a login
that belongs to Scribe rather than to you, granted access to just this one resource.

Choose this when your organisation's policy forbids key authentication, or when you want access
governed by a role that an administrator can revoke centrally. It needs permission to register an
application in your tenant, which not every tenant grants.

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
2. Set **Sign-in method** to **API key**.
3. Paste the **API key**, the **Endpoint**, and type the **Deployment name**.
4. Save, then dictate something into Notepad to confirm.

The key is encrypted on your PC with Windows DPAPI, scoped to your user account, so no other user on
the machine can read it. Scribe never writes it to an environment variable or a file on disk.

### If you used `-UseServicePrincipal` instead

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

Put the expiry date in your calendar. The default secret lasts a year, and when it lapses cleanup
starts failing for a reason nobody remembers. Rotate it with:

```powershell
az ad app credential reset --id <client-id> --years 1
```

An API key, by contrast, does not expire.

## When something goes wrong

Scribe reports the HTTP status behind any cleanup failure, and that status is the fastest way to
narrow things down. A **403 means your credentials were accepted and access was refused**, so
nothing about the secret or the tenant is the problem. A **404 means access was fine and the
deployment name was not found**. Read the status before changing anything.

| What you see | What it means | What to do |
| --- | --- | --- |
| Script says the CLI is too old | `--allow-project-management` is missing | Run `az upgrade`, then re-run the script |
| No subscriptions listed | Credits not activated yet | [my.visualstudio.com/benefits](https://my.visualstudio.com/benefits), activate Azure, re-run |
| Wrong account signed in | The subscription belongs to a different account than `az login` used | Sign out and back in with the account that owns the credits. The script names both accounts when it detects this |
| No regions offered the model | No quota for that model on this subscription | The script falls back automatically. To force one, pass `-Model gpt-5-mini` or `-Model gpt-5-nano` |
| Cannot register applications | Your tenant restricts app registration | Ask your admin for the **Application Developer** role |
| `AADSTS700016` | The app registration is in a different tenant than the one entered | Check the tenant ID matches the directory that owns the registration |
| `AADSTS7000215` invalid secret | Usually a brand new secret that has not propagated | Wait a minute, verify again. Then check you copied the secret Value, not the Secret ID |
| 401 Unauthorized | The token was rejected | Confirm the resource has a custom subdomain (`services.ai.azure.com`, not a regional address) |
| Could not read an API key, or `disableLocalAuth` is true | The subscription or resource policy disables local key authentication | Use service principal sign-in instead. Scribe only needs the Foundry User role on the resource |
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
