# Set up a service principal for AI cleanup

Scribe's AI cleanup can call a model in Microsoft Foundry or Azure OpenAI. By default it borrows
whichever account you signed into with `az login`. That is convenient, but if you belong to more
than one tenant it is also unpredictable: the Azure CLI has a single active account, and the wrong
one produces the confusing error `AADSTS700016: Application not found in tenant`.

A **service principal** fixes that. It is an identity that belongs to your app rather than to you,
so Scribe authenticates as exactly the same identity every time, in exactly the tenant you choose.

Dictation itself never needs any of this. Scribe transcribes offline on your own machine, and AI
cleanup is optional. Only the cleanup step talks to Azure, and it sends the transcribed text only,
never audio.

> **Starting from nothing?** If you do not yet have a Foundry resource with a deployed model, use
> [foundry-setup.md](foundry-setup.md) instead. That guide has a script that creates the resource,
> the project, the model deployment **and** the service principal in one run, so you do not need to
> do any of the steps below by hand. Come back here if you would rather do it manually, or if you
> need the portal walkthrough or the troubleshooting detail.

## What you will need

- An Azure subscription containing a Microsoft Foundry or Azure OpenAI resource with a deployed
  chat model.
- Permission to register an application in your tenant. Many corporate tenants restrict this. If
  yours does, you will see "You don't have permission to register applications in the <directory>
  directory" and you will need your administrator to either grant you the **Application Developer**
  role or create the registration for you.
- Permission to assign a role on the resource, which means **Owner** or **User Access
  Administrator** at that scope. If you do not have it, ask whoever administers the resource to do
  step 3 for you.

## Step 1: register the application

Portal:

1. Sign in to the [Microsoft Entra admin center](https://entra.microsoft.com).
2. If you belong to several tenants, use the Settings icon in the top bar to switch to the tenant
   that owns your model. This is the step people miss, and it is the reason for most
   "Application not found in tenant" errors later.
3. Go to **Entra ID > App registrations > New registration**.
4. Give it a name, for example `Scribe-AI-Cleanup`.
5. For **Supported account types**, choose **Single tenant**. Scribe does not need anything wider.
6. Select **Register**.
7. On the Overview page, copy the **Application (client) ID** and the **Directory (tenant) ID**.
   These are the first two values Scribe asks for.

Azure CLI equivalent, which also creates the secret in step 2:

```bash
az login
az ad sp create-for-rbac --name "Scribe-AI-Cleanup" --years 1
```

The output contains `appId` (the client ID), `password` (the client secret) and `tenant` (the
tenant ID). Treat that output as a credential: do not paste it into a chat, a ticket, or a file
you keep.

By default this command assigns no permissions at all, which is what you want. Step 3 grants the
one role that is actually needed.

If you would rather create the registration from the CLI but generate the secret in the portal,
add `--create-password false` and then follow step 2:

```bash
az ad sp create-for-rbac --name "Scribe-AI-Cleanup" --create-password false
```

## Step 2: create a client secret

Portal:

1. In your app registration, go to **Certificates & secrets > Client secrets > New client secret**.
2. Add a description and choose an expiry. The maximum is 24 months, and Microsoft recommends less
   than 12.
3. Select **Add**.
4. Copy the **Value** column immediately. Azure shows it exactly once and never again. The Secret
   ID next to it is not the secret; if you copy that one, authentication will fail.

**A brand new secret does not work instantly.** For up to a minute or two Entra rejects it with
`AADSTS7000215: Invalid client secret provided`, which reads as though you typed it wrong. You
probably did not. Wait a moment and select Verify again. This is measurable rather than folklore:
the same secret can fail and then succeed roughly thirty seconds later.

Put a reminder in your calendar for the expiry date. When a secret expires, cleanup starts failing
and the cause is not obvious. Rotate it with `az ad sp credential reset`.

If your tenant blocks client secrets by policy, you will need a certificate instead. Scribe does not
support certificate credentials yet; open an issue if you need it.

## Step 3: grant access to the model

This is the step that actually lets Scribe call your model, and it is the one most often missed.
Registering an app grants it nothing by itself.

Assign the role on the **Foundry or Azure OpenAI resource itself**, not on the subscription. Scribe
does not enumerate your subscriptions when you use a service principal, precisely so that you do not
have to hand out subscription-wide access.

| Your resource | Role to assign | Role ID |
| --- | --- | --- |
| Microsoft Foundry (`AIServices` kind, `*.services.ai.azure.com`, including project endpoints) | **Foundry User** | `53ca6127-db72-4b80-b1b0-d745d6d5456d` |
| Azure OpenAI account (`OpenAI` kind, `*.openai.azure.com`) | Cognitive Services OpenAI User | `5e0bd9bd-7b93-4f28-af87-19fc36ad61bd` |

Not sure which you have? Check the `kind` field:

```bash
az cognitiveservices account show -n "<ACCOUNT>" -g "<RG>" --query kind -o tsv
```

`AIServices` means Foundry. `OpenAI` means an Azure OpenAI account.

### Do not use the Cognitive Services roles on a Foundry resource

Microsoft's Foundry RBAC guidance is explicit:

> Don't assign built-in roles that start with **Cognitive Services**. These roles are designed for
> accessing AI Services resources directly and don't apply to Foundry scenarios. Similarly, don't use
> the **Azure AI Developer** role for Foundry work. Despite the name, this role is scoped to Azure
> Machine Learning workspaces and Foundry hubs, not to Foundry projects or Foundry hosted agents. For
> Foundry project access, use **Foundry User** or **Foundry Owner** instead.
>
> Source: [Role-based access control for Microsoft Foundry](https://learn.microsoft.com/azure/foundry/concepts/rbac-foundry)

`Cognitive Services User` may currently still work against a Foundry endpoint, but it is not the
supported path and it is not what to build on. **Foundry User** is the least-privilege Foundry role
and is all Scribe needs: it grants data-plane access to call a deployed model, and nothing else. It
cannot deploy models, create projects, or assign roles.

### The Foundry roles were renamed

`Azure AI User` is not a different role from `Foundry User`. It is the **old name for the same
role**. The same is true across the family, and the IDs did not change:

| Old name | New name | Role ID |
| --- | --- | --- |
| Azure AI User | Foundry User | `53ca6127-db72-4b80-b1b0-d745d6d5456d` |
| Azure AI Project Manager | Foundry Project Manager | `eadc314b-1a2d-4efa-be10-5d325db5065e` |
| Azure AI Account Owner | Foundry Account Owner | `e47c6f54-e4a2-4754-9501-8e0985b135e1` |
| Azure AI Owner | Foundry Owner | `c883944f-8b7b-4483-af10-35834be79c4a` |

Microsoft recommends assigning **by role ID rather than by name** while the rename rolls out, because
a portal or CLI may still show either name. Every command in this guide does that.

### Roles that sound right and are not

- **Azure AI Developer** targets Azure Machine Learning workspaces and Foundry hubs, not Foundry
  projects. Microsoft names it directly as one to avoid.
- **Azure AI Inference Deployment Operator** grants no data actions at all. Despite the name it is
  about deploying Azure resources, not calling a model.
- **Cognitive Services Contributor** can create deployments but cannot call them.
- **Foundry Project Manager** cannot deploy models, despite one Microsoft scenario table suggesting
  otherwise. The per-permission reference is authoritative. Scribe never deploys, so this does not
  affect setup here, but it trips people up when they are also creating the deployment.

Portal:

1. Open your Foundry or Azure OpenAI resource in the Azure portal.
2. Go to **Access control (IAM) > Add > Add role assignment**.
3. Pick the role from the table above. If your portal still shows **Azure AI User**, that is
   **Foundry User**; the rename is mid-rollout.
4. On the Members tab choose **User, group, or service principal**, then select your app
   registration by name.
5. Select **Review + assign**.

**Then wait, and expect it to take longer than you think.** Microsoft documents role assignments as
taking up to five minutes. In practice a fresh assignment on a Foundry resource has taken closer to
ten, and during that window the call fails with a 403 that looks exactly like a wrong role. If you
have just assigned a role and are still seeing 403, the single most likely explanation is that you
have not waited long enough. Verify the assignment exists (below), then wait rather than reassigning
a different role and losing track of which change did what.

Azure CLI equivalent:

```bash
# Object ID of the service principal, which is not the same as the application (client) ID.
OID=$(az ad sp show --id "<APPLICATION_CLIENT_ID>" --query id -o tsv)

# Foundry User. For an Azure OpenAI account use 5e0bd9bd-7b93-4f28-af87-19fc36ad61bd instead.
az role assignment create \
  --assignee-object-id "$OID" \
  --assignee-principal-type ServicePrincipal \
  --role "53ca6127-db72-4b80-b1b0-d745d6d5456d" \
  --scope "/subscriptions/<SUB_ID>/resourceGroups/<RG>/providers/Microsoft.CognitiveServices/accounts/<ACCOUNT>"
```

Use `--assignee-object-id` with `--assignee-principal-type ServicePrincipal` rather than
`--assignee`. The `--assignee` form does a directory lookup that fails or silently resolves to the
wrong object in tenants where you cannot read all principals.

The role goes on the **account resource** even when Scribe is pointed at a project endpoint. A
project is not a separate assignment scope for this, so there is nothing extra to grant per project.

Verify it landed:

```bash
az role assignment list \
  --assignee "<APPLICATION_CLIENT_ID>" \
  --scope "/subscriptions/<SUB_ID>/resourceGroups/<RG>/providers/Microsoft.CognitiveServices/accounts/<ACCOUNT>" \
  --query "[].roleDefinitionName" -o tsv
```

An empty result means the assignment is not there, which is a different problem from propagation and
is worth separating before you start waiting.

If you are cleaning up an older setup, remove any `Cognitive Services *` assignments once Foundry
User is in place and verified:

```bash
az role assignment delete \
  --assignee "<APPLICATION_CLIENT_ID>" \
  --role "a97b65f3-24c7-4388-baec-2e87135dc908" \
  --scope "<RESOURCE_SCOPE>"
```

## Step 4: check the resource has a custom subdomain

Microsoft Entra authentication only works against a custom subdomain such as
`https://my-resource.openai.azure.com/`. A regional endpoint like
`https://eastus.api.cognitive.microsoft.com/` will reject the token no matter how the roles are set.
If your resource uses a regional endpoint, add a custom subdomain to it before continuing.

## Step 5: enter the details in Scribe

1. Open Scribe's settings and go to the AI cleanup section.
2. Set the provider to Microsoft Foundry.
3. Set **Sign-in method** to **Service principal**.
4. Enter the directory (tenant) ID, application (client) ID, and the client secret Value.
5. Select **Verify service principal**. Scribe requests a real token, so this either confirms the
   identity works or tells you it does not.
6. Once verified, enter the **Endpoint** and the **Deployment name** of your model. Service
   principal mode does not browse your subscriptions, so these are typed in rather than picked from
   a list.
7. Save.

### Which endpoint to enter

Scribe accepts both shapes, and either works with a service principal:

| Shape | Example | Notes |
| --- | --- | --- |
| Foundry **project** endpoint | `https://my-resource.services.ai.azure.com/api/projects/my-project` | Microsoft's recommended form. Scribe routes it natively. Entra only, no API keys. |
| Account endpoint | `https://my-resource.services.ai.azure.com` or `https://my-resource.openai.azure.com` | Works with Entra or an API key. |

The project endpoint is on the project's overview page in the Foundry portal; the account endpoint is
on the resource's overview page in the Azure portal. Both are shown in Foundry, which is a common
source of confusion, so copy the one whose page you are actually on.

The **deployment name** is the name you gave the model when you deployed it, not the model's own
name. These match by default and often diverge later. If the same model is deployed on two resources
the names are frequently different, for example `gpt-5.6-terra` on one and `gpt-5.6-terra-usdz` on
another. A deployment name that belongs to a different resource than the endpoint produces a 404,
not a 403, and Scribe now says so explicitly.

The secret is encrypted on your PC with Windows DPAPI, scoped to your user account, so no other user
on the machine can read it. Scribe never writes it to an environment variable, a `.env` file, or a
script. Those are plain text on disk, and persistent `AZURE_CLIENT_*` variables would additionally
change how every other Azure tool on your machine picks its credentials.

## When something goes wrong

Scribe reports the HTTP status behind a cleanup failure, and the status is the fastest way to tell
these apart. A 403 means your credentials were accepted and access was refused, so nothing about the
secret, the tenant, or `az login` is the problem. A 404 means access was fine and the deployment name
was not found. Reading the status first saves changing the wrong thing.

| What you see | What it means | What to do |
| --- | --- | --- |
| `AADSTS700016` application not found | The app registration is in a different tenant than the one entered | Confirm the tenant ID matches the directory that owns the app registration |
| `AADSTS7000215` invalid client secret | Usually a secret created moments ago that has not propagated. Otherwise the Secret ID was copied instead of the Value | Wait a minute and verify again, then check you copied the Value column |
| 401 Unauthorized | The token itself was rejected | Check the resource has a custom subdomain (step 4) |
| 403 Forbidden, right after assigning a role | Authorization has not propagated yet | Confirm the assignment exists, then wait. Ten minutes is normal; do not start swapping roles |
| 403 Forbidden, and the assignment is minutes old or more | The role is missing, on the wrong resource, or is one of the look-alike roles | Re-check step 3, and confirm the scope is the resource that hosts the deployment |
| 403 Forbidden on a Foundry resource with a `Cognitive Services *` role | That role family is not supported for Foundry | Assign **Foundry User** (`53ca6127-db72-4b80-b1b0-d745d6d5456d`) instead |
| 404 Not found | Endpoint and credentials are fine; the deployment name does not exist on that resource | Check the deployment name against that specific resource, including any suffix |
| 429 Too many requests | The deployment is correct but over quota | Wait, or raise the deployment's capacity |
| Verification succeeds but cleanup fails | The identity is valid but cannot call the model | Verification only proves the identity works. A 403 here still means step 3 |
| Cleanup worked and then stopped | The client secret expired | Rotate it with `az ad sp credential reset` and update Scribe |

## If you would rather not do any of this

Service principals are worth it when you live in several tenants. If you only use one, the Azure CLI
option needs no setup beyond `az login`, and an API key needs no Entra configuration at all. Both
remain fully supported.
