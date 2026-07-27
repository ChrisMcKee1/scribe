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

Which role depends on the kind of resource:

| Your resource | Role to assign | Role ID |
| --- | --- | --- |
| Azure OpenAI account (`*.openai.azure.com`) | Cognitive Services OpenAI User | `5e0bd9bd-7b93-4f28-af87-19fc36ad61bd` |
| Microsoft Foundry resource or project | Cognitive Services User | `a97b65f3-24c7-4388-baec-2e87135dc908` |

Assign by role ID rather than by name. Microsoft renamed several Foundry roles recently (Azure AI
User became Foundry User, and so on) and the IDs are the part that did not change.

Two roles that sound right and are not:

- **Azure AI Inference Deployment Operator** grants no data actions at all. Despite the name it is
  about deploying Azure resources, not calling a model.
- **Cognitive Services Contributor** can create deployments but cannot call them.

Portal:

1. Open your Foundry or Azure OpenAI resource in the Azure portal.
2. Go to **Access control (IAM) > Add > Add role assignment**.
3. Pick the role from the table above.
4. On the Members tab choose **User, group, or service principal**, then select your app
   registration by name.
5. Select **Review + assign**, then wait up to five minutes for the assignment to take effect.

Azure CLI equivalent:

```bash
az role assignment create \
  --assignee-object-id "<SERVICE_PRINCIPAL_OBJECT_ID>" \
  --assignee-principal-type ServicePrincipal \
  --role "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd" \
  --scope "/subscriptions/<SUB_ID>/resourceGroups/<RG>/providers/Microsoft.CognitiveServices/accounts/<ACCOUNT>"
```

Verify it landed:

```bash
az role assignment list --assignee <SERVICE_PRINCIPAL_OBJECT_ID> --scope <RESOURCE_SCOPE>
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
   a list. The endpoint is on your resource's overview page, and the deployment name is the name you
   gave the model when you deployed it, not the model's own name.
7. Save.

The secret is encrypted on your PC with Windows DPAPI, scoped to your user account, so no other user
on the machine can read it. Scribe never writes it to an environment variable, a `.env` file, or a
script. Those are plain text on disk, and persistent `AZURE_CLIENT_*` variables would additionally
change how every other Azure tool on your machine picks its credentials.

## When something goes wrong

| What you see | What it means | What to do |
| --- | --- | --- |
| `AADSTS700016` application not found | The app registration is in a different tenant than the one entered | Confirm the tenant ID matches the directory that owns the app registration |
| `AADSTS7000215` invalid client secret | The secret is wrong, or the Secret ID was copied instead of the Value | Create a new secret and copy the Value column |
| 401 Unauthorized | The token was rejected | Check the resource has a custom subdomain (step 4) |
| 403 Forbidden | Authentication worked, authorization did not | The role assignment in step 3 is missing, on the wrong resource, or has not propagated yet |
| Verification succeeds but cleanup fails | The identity is valid but cannot call the model | The role in step 3 is probably one of the two look-alike roles that grant no data actions |
| Cleanup worked and then stopped | The client secret expired | Rotate it with `az ad sp credential reset` and update Scribe |

## If you would rather not do any of this

Service principals are worth it when you live in several tenants. If you only use one, the Azure CLI
option needs no setup beyond `az login`, and an API key needs no Entra configuration at all. Both
remain fully supported.
