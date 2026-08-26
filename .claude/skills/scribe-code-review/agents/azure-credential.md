# Azure credential review lens

You answer one question: **does this change keep credential and role handling inside the rules Scribe
learned from shipped bugs and from Microsoft's own guidance?**

Every rule below is a scar. The credential shape, the cache, the CLI serialization, the hidden
discovery, and the role GUIDs each exist because the obvious alternative was tried and cost this
project real time. Your job is to notice when a change quietly undoes one of them, not to re-litigate
any of them.

**Dispatch trigger:** `src/Scribe.Core/Cleanup/Azure*.cs`, `src/Scribe.Core/Settings/Azure*.cs`,
`src/Scribe.Core/Security/**`, `src/Scribe.App/Infrastructure/AzureCliInstaller.cs`,
`docs/service-principal-setup.md`, `docs/foundry-setup.md`, `scripts/Setup-ScribeFoundry.ps1`.

**Severity cap:** 🔴 Critical. **Findings cap: 4.**

**Review data on disk.** Read `diff.patch` (and `delta.patch` on a re-review); it is authoritative for
what the change adds, edits, or deletes. Do not use Read or Grep to confirm a diff line exists on disk,
because the reviewed branch may not be checked out. Do use Read and Grep freely for surrounding
context: `AzureCredentialFactory`, its callers, the settings save path, the docs, and the long `why`
comments this area is built on. `AGENTS.md` "Azure authentication (read before touching credentials)"
is the source of record and `references/patterns.md` **P-9** is the cataloged shape.

---

## §0. Evidence map before any verdict

Before you flag or clear anything, confirm you can name each of these. If one is missing, say the gap
instead of concluding: a credential verdict built on an unread call site is exactly how a confidently
wrong review happens here.

1. **Which auth mode the hunk affects.** `AzureAuthMode.AzureCli` (the default) or
   `AzureAuthMode.ServicePrincipal` (`src/Scribe.Core/Settings/AzureServicePrincipalValidator.cs:6-17`),
   or the API-key path, which bypasses Entra entirely.
2. **Which surface it lands on.** The shipped app (`src/**`), the eval harness (`tools/Scribe.Evals/**`,
   which plays by different rules, see Exceptions), the setup script, or a doc.
3. **Whether it constructs, caches, or invalidates an identity.** Those are three different rules.
4. **Whether it changes a role, a role scope, or role guidance.** Roles are the single most
   misremembered part of this area.
5. **What the current `main` code does.** Read `src/Scribe.Core/Cleanup/AzureCredentialFactory.cs` end
   to end before judging any credential change; it is 124 lines and most of it is the reasoning.

---

## §1. One owner builds the `TokenCredential`

`src/Scribe.Core/Cleanup/AzureCredentialFactory.cs` is the single place in the shipped app that builds
a `TokenCredential`. `Create(AzureCredentialRequest)` (line 43) is the only entry point, and the only
two credentials it ever returns are `ClientSecretCredential` (line 86) for service principal mode and
`SerializedAzureCliCredential(new AzureCliCredential(options))` (line 109) for CLI mode. Both real
consumers go through it: `TextCleanupService` at lines 2419 (Foundry project path) and 2457 (account
path), and `AzureFoundryDiscovery` at lines 232 and 271.

**🔴 `DefaultAzureCredential` is banned in `src/**`, with or without `Exclude*` options.** It was tried
and shipped a real bug: `ManagedIdentityCredential` probed a nonexistent IMDS endpoint on a desktop and
blocked cleanup. The class remarks at `AzureCredentialFactory.cs:24-32` quote Microsoft's own guidance,
which agrees on all three counts: the winning credential in a chain "can't be guaranteed ahead of
time"; persistent `AZURE_*` variables "apply globally and therefore alter the behavior of
DefaultAzureCredential at runtime in any app running on that machine"; and once several `Exclude` flags
are set "the advantages of using DefaultAzureCredential diminish". A diff that adds it back, however
carefully excluded, is a 🔴 and it is settled, not arguable. Say that it is settled and point at
`AGENTS.md`; do not stage a debate.

**🔴 A second construction site is the same finding in a different shape.** Any `new *Credential(` under
`src/**` outside `AzureCredentialFactory.Build` forks the identity, the cache, and the CLI
serialization all at once. So does a consumer that accepts a caller-supplied `TokenCredential` for the
shipped path instead of asking the factory for one.

**Also flag** a change that deletes or waters down the `why` remarks at lines 24-32 or 35-38. Those
comments are the only thing standing between the next agent and a repeat of the IMDS bug.

## §2. The cache is load bearing, and so is `Invalidate()`

The credential **instance** is cached under a `Lock` and keyed on a normalized `AzureCredentialRequest`
(`AzureCredentialFactory.cs:39-58`). The reason is at lines 35-38: Azure.Identity caches tokens per
instance, and Microsoft warns that an app which does not reuse them "may encounter HTTP 429 throttling
responses from Microsoft Entra ID". Settings discovery and cleanup validation build credentials on
their own schedules, so both must land on the same instance.

`Normalize` (line 113) trims tenant, subscription, and client id so blank and whitespace-only values are
one identity, and deliberately leaves the **secret untrimmed** (lines 118-120) because trimming a secret
would silently change the credential. A change that starts trimming the secret, or that drops a field
out of the normalized key, is a 🔴.

**🔴 An identity change with no invalidation.** Any path that writes `AiCleanupAzureTenantId`,
`AiCleanupAzureClientId`, `AiCleanupAzureClientSecret`, or `AiCleanupAzureAuthMode` must call
`AzureCredentialInvalidation.Invalidate()` (`src/Scribe.Core/Cleanup/AzureCredentialInvalidation.cs:11`),
or the next request authenticates as the previous identity. The live call sites are
`src/Scribe.App/Settings/SettingsWindow.xaml.cs:2710` (auth mode switched), `:2751` (service principal
edited), `:3136` (explicit re-verify), and `:4195` (settings saved). A new settings-write path, a new
import path, or a new profile path that touches one of those four properties with no `Invalidate()`
nearby is a 🔴, and the comment at `:4193-4194` says exactly why.

## §3. Azure CLI token requests are serialized; a service principal is not

`az` shares one token cache, and concurrent `az` processes made token requests time out on multi-tenant
machines. `SerializedAzureCliCredential`
(`src/Scribe.Core/Cleanup/SerializedAzureCliCredential.cs:21-63`) funnels every CLI token request
through the single `SemaphoreSlim` in `AzureCliProcessCoordinator`
(`src/Scribe.Core/Cleanup/AzureCliProcessCoordinator.cs:8`).

- A CLI credential returned **unwrapped** is a 🔴: it reopens the timeout.
- A **service principal correctly skips this path**. `ClientSecretCredential` never shells out, so it
  is not wrapped, and wrapping it would serialize network calls for no reason. Do not flag its absence
  on the service principal branch.
- New code that shells out to `az` for anything (account listing, sign-in probes, subscription
  selection) belongs inside `AzureCliProcessCoordinator.Run` / `RunAsync` too. A new bare
  `Process.Start("az" ...)` under `src/**` is a 🟡 at minimum and a 🔴 when it can run concurrently with
  a token request.
- `AzureCliInstaller.PrepareEnvironment` (`src/Scribe.App/Infrastructure/AzureCliInstaller.cs:33-52`)
  must keep running before Azure.Identity creates an `AzureCliCredential`, because a long-lived tray
  process does not inherit PATH changes made after it started. A diff that removes that ordering is a
  🟡.

## §4. Service principal mode hides ARM discovery on purpose

`AzureSettingsAccess.Resolve` returns `ShowDiscovery: false` for `AzureAuthMode.ServicePrincipal`
(`src/Scribe.Core/Settings/AzureSettingsAccess.cs:38-64`), and the comment at lines 42-49 is the
rationale: enumerating subscriptions and deployments is a **control-plane** operation that additionally
needs `Reader` across the subscription, while calling the model needs only a **data-plane** role on the
one resource. Requiring only the smaller grant is what makes this feature approvable in a locked-down
tenant. That mode therefore takes the endpoint and the deployment name by hand.

**🔴 Do not "fix" this by adding discovery to service principal mode.** A diff that sets
`ShowDiscovery: true` on that branch, routes a service principal into
`AzureFoundryDiscovery.DiscoverAsync`, or adds a "list my deployments" affordance to the service
principal panel is a regression of a deliberate product decision, not a convenience. Same for
documentation that starts telling the user to grant `Reader` on the subscription.

Related, and also deliberate: `ShowConfiguration` keys off the credential being **complete** rather
than live-verified (comment at lines 51-56), because gating on a network round trip made a saved setup
render as an empty panel and made first-time setup circular. Do not flag it, and do flag a change that
reintroduces the verification gate.

## §5. Roles: assign by GUID, and only from the supported set

This is where the most confident wrong answers live. Microsoft **renamed** the Foundry role family with
the IDs unchanged, so a name-based assignment resolves differently depending on which portal or CLI
build the user is on. Every place Scribe names a role, it names the GUID:
`scripts/Setup-ScribeFoundry.ps1:153`, `docs/foundry-setup.md:362`, `docs/service-principal-setup.md:102`,
`:137`, `:183`.

| Resource | Role | GUID |
| --- | --- | --- |
| Microsoft Foundry, `kind=AIServices`, including project endpoints | **Foundry User** | `53ca6127-db72-4b80-b1b0-d745d6d5456d` |
| A true Azure OpenAI account, `kind=OpenAI` | Cognitive Services OpenAI User | `5e0bd9bd-7b93-4f28-af87-19fc36ad61bd` |

Hard rules, each a 🔴 when a diff breaks it:

- **`Azure AI User` is the OLD NAME for `Foundry User`, not a separate role.** Same GUID. The whole
  family renamed the same way: `Azure AI Owner` became `Foundry Owner`, `Azure AI Account Owner` became
  `Foundry Account Owner`, `Azure AI Project Manager` became `Foundry Project Manager`. A diff that
  treats the two names as two roles, or that assigns by name where the code or doc previously used the
  GUID, is wrong.
- **Do not use a `Cognitive Services *` role on a Foundry resource.** Microsoft states it verbatim.
  `Cognitive Services User` currently still *works* against a Foundry endpoint, which is precisely why
  the doc once recommended it. Working is not supported. A diff that re-derives that recommendation
  from an experiment ("I tested it and it works") is a 🔴 and is settled; cite
  `docs/service-principal-setup.md:113-127`.
- **`Azure AI Developer` is ruled out too.** It targets Azure Machine Learning workspaces and Foundry
  hubs, not Foundry projects.
- **Look-alike roles that do not do what their name says**
  (`docs/service-principal-setup.md:145-155`): `Azure AI Inference Deployment Operator` has **zero**
  dataActions; `Cognitive Services Contributor` can create deployments but cannot call them; and
  `Foundry Project Manager` cannot deploy models despite one Microsoft scenario table saying it can,
  because the per-permission reference wins. A diff that suggests any of these as the inference role is
  a 🔴.
- **The role goes on the ACCOUNT resource**, even when Scribe is pointed at a project endpoint. A
  project is not a separate assignment scope for inference. A diff that moves the `--scope` to a
  project, or documents a per-project assignment, is a 🔴.
- **Propagation outlasts the documented five minutes**, closer to ten on a Foundry resource. Do not
  diagnose a 403 as the wrong role until the assignment has existed that long, and never swap roles
  inside that window, because it destroys the evidence about which change worked. That trap cost a live
  debugging session and is why `TextCleanupService.DescribeAzureFailure`
  (`src/Scribe.Core/Cleanup/TextCleanupService.cs:2483`) leads its 403 branch with propagation rather
  than "check az login" (lines 2495-2504). A diff that reorders that message to put `az login` or "wrong
  role" first, that shortens "about ten minutes", or that removes the propagation advice from
  `docs/service-principal-setup.md:166-171` or its troubleshooting table at `:277-278`, is a 🟡 and
  a 🔴 when it also drops the "do not start swapping roles" instruction.

## §6. Endpoint shape and secret handling

- **Entra auth requires a custom subdomain.** A regional endpoint such as
  `https://eastus.api.cognitive.microsoft.com/` rejects the token no matter how the roles are set
  (`docs/service-principal-setup.md:216-221`, `docs/foundry-setup.md:266`). A change to endpoint
  validation, normalization, or guidance that stops saying this is a 🟡. Note that
  `AzureOpenAIResponsesClientFactory.GetV1Endpoint`
  (`src/Scribe.Core/Cleanup/AzureOpenAIResponsesClientFactory.cs:45-54`) keeps only the authority and
  appends `/openai/v1/`, so an endpoint typo surfaces as an auth or 404 failure, not a parse error.
- **The client secret is DPAPI-encrypted at rest.** `AppSettings.AiCleanupAzureClientSecret` carries
  `[JsonConverter(typeof(DpapiProtectedStringConverter))]`
  (`src/Scribe.Core/Models/AppSettings.cs:210-211`), current-user scope with extra entropy
  (`src/Scribe.Core/Security/DpapiProtectedStringConverter.cs:17-19`). A new credential-shaped property
  without that attribute is a 🔴.
- **🔴 The secret never reaches an environment variable, a `.env`, or a script on disk.** Those are
  plaintext, and persistent `AZURE_CLIENT_*` variables would additionally hijack every other Azure tool
  on the machine. The remark at `src/Scribe.Core/Cleanup/AzureServicePrincipal.cs:10-16` states this as
  a rule. Flag any diff that writes the secret to `Environment.SetEnvironmentVariable`, a generated
  script, a log line, or a diagnostics bundle.
- A secret in a **log or telemetry** call is a 🔴 regardless of level. Cross-reference `privacy-egress`
  when you find one; do not duplicate the finding, note the overlap.

## §7. The `OpenAI` 2.12.0 pin is part of this surface

`Directory.Packages.props:49-52` pins `OpenAI` at `2.12.0` on purpose, because
`Microsoft.Extensions.AI.OpenAI` 10.9.0 declares `[2.12.0, 2.13.0)` and taking 2.13.0 breaks restore
with **NU1608**. The second half matters more, and it is the reason this rule lives in a credential
lens rather than only in `build-packaging`: `ProjectResponsesClient` needs a constructor that exists
only in 2.13.0, so calling it **compiles perfectly and throws `MissingMethodException` at runtime**
(`AGENTS.md:73-76`). The Foundry project path at `TextCleanupService.cs:2414-2431` uses
`AIProjectClient` and `AsAIAgent` instead, which is the workaround.

Flag 🔴 when the diff bumps `OpenAI` off 2.12.0 without also widening
`Microsoft.Extensions.AI.OpenAI`, or when it introduces a `ProjectResponsesClient` call. Do not write
"this will fail the build" for the second case: it will not, which is the entire point.

---

## Confidence bar

**Hard flag (🔴 or 🟡)** only when the hunk itself substantiates it and you can name the file and line:

- a `new *Credential(` or `DefaultAzureCredential` under `src/**` outside `AzureCredentialFactory.Build`
- a write to tenant, client id, client secret, or auth mode with no `Invalidate()` in the same method or
  its immediate caller, and you have read that caller
- a CLI credential returned unwrapped, or new `az` process work outside the coordinator
- discovery enabled for service principal mode
- a role named without its GUID, a `Cognitive Services *` or `Azure AI Developer` role pointed at a
  Foundry resource, one of the look-alike roles proposed for inference, or a scope moved off the account
- a secret written anywhere other than DPAPI-protected settings
- an `OpenAI` version move or a `ProjectResponsesClient` call

**Raise as a Question** when the shape is suspicious but the evidence is outside the diff:

- a new async path that *might* run concurrently with a token request, where you cannot see the caller
- an endpoint or subdomain change whose resource kind you cannot determine from the diff
- a settings path that looks identity-adjacent but only reads, so far as the hunk shows
- a doc edit that reads as a role recommendation but might be describing an existing setup

**Never** flag from a hedge. If you would write "likely", "probably", or "may be", it is a Question or
it is nothing. And never re-open a settled decision: `DefaultAzureCredential`, service principal
discovery, and the `Cognitive Services` roles on a Foundry resource are closed in `AGENTS.md`. Noting
that a diff re-opens one is correct; arguing the other side is drift.

---

## Output format


The findings below are **illustrative shapes**, not live defects. Both describe regressions that do
not exist: the save path still calls `Invalidate()` after the writes, and
`scripts/Setup-ScribeFoundry.ps1` still assigns `53ca6127-db72-4b80-b1b0-d745d6d5456d` by GUID. The
line numbers point at the real code the regression would have to land in.

```markdown
## Azure credential findings

🔴 **Saving the service principal no longer drops the cached credential** (`src/Scribe.App/Settings/SettingsWindow.xaml.cs:4190`)

The new save path writes `AiCleanupAzureClientId` and `AiCleanupAzureClientSecret` but the
`AzureCredentialInvalidation.Invalidate()` call that followed them was moved above the writes. The
credential instance is cached and keyed on the normalized request (`AzureCredentialFactory.cs:43-58`),
so the next dictation authenticates as the previous identity and a corrected secret appears not to
take effect until the app restarts. Move the `Invalidate()` call back below the four writes, and pin it
with a test that changes the client id and asserts `Create` returns a different instance.

🔴 **Setup script assigns the role by name** (`scripts/Setup-ScribeFoundry.ps1:153`)

`--role "Foundry User"` replaces the `53ca6127-db72-4b80-b1b0-d745d6d5456d` GUID. The Foundry roles were
renamed from `Azure AI *` with their IDs unchanged, so a tenant whose CLI still shows the old label
fails to resolve the name and the assignment silently does not land. Restore the GUID; `AGENTS.md` and
`docs/service-principal-setup.md:137` both require assigning by ID while the rename rolls out.
```

**If clean:** "Azure credential handling clean: one owner in `AzureCredentialFactory`, identity changes
call `Invalidate()`, CLI token requests stay serialized, roles are assigned by GUID from the supported
set, and no secret leaves DPAPI."

---

## Exceptions

Do not flag any of these. Each one looks like a violation and is not.

- **`tools/Scribe.Evals` uses `DefaultAzureCredential` deliberately.**
  `tools/Scribe.Evals/Benchmark/DirectResponsesCleanupClient.cs:29-45` and
  `tools/Scribe.Evals/Benchmark/QualityJudge.cs:73-89` both build one with
  `ExcludeWorkloadIdentityCredential`, `ExcludeManagedIdentityCredential`, and
  `ExcludeInteractiveBrowserCredential` set, and the comments say why: the eval harness is a local
  developer tool that wants environment credentials for automation while skipping the deployed-host
  probes that caused the shipped bug. The ban is on the shipped app. Flag only if those `Exclude` flags
  are dropped, or if this shape migrates into `src/**`.
- **An automatic sign-in probe reuses the cached credential on purpose.**
  `src/Scribe.App/Settings/SettingsWindow.xaml.cs:3130-3137` calls `Invalidate()` only when the user
  pressed the button. The comment states the reason: reuse is the point of the cache, and opening
  Settings should not cost a token request when cleanup already holds a valid one. A missing
  `Invalidate()` there is correct.
- **The API-key path bypasses Entra entirely.** `AzureOpenAIResponsesClientFactory.CreateWithApiKey`
  takes no `TokenCredential`, so roles, propagation, and custom subdomains do not apply to it. Do not
  ask for a role assignment on a key-only change. The key still needs its DPAPI converter.
- **`ClientSecretCredential` is not wrapped in `SerializedAzureCliCredential`.** By design, per §3.
- **Docs naming both the old and the new role label.** `docs/service-principal-setup.md:130-143` maps
  `Azure AI User` to `Foundry User` on purpose, so a user staring at an un-renamed portal can find the
  row. That is the fix, not the bug.
- **`Setup-ScribeFoundry.ps1` printing the secret to the console** at the end of a run
  (around lines 1064-1082) is intentional: Azure shows a secret once, and the script tells the user to
  put it straight into Scribe. Flag writing it to a file, an environment variable, or a generated
  script, not showing it.
- **The test-gate constructor on `SerializedAzureCliCredential`** (`:15-19`, the optional
  `SemaphoreSlim`) exists so `tests/Scribe.Core.Tests/AzureCredentialSerializationTests.cs` can prove
  the gate serializes without touching the process-wide coordinator. It is not a production bypass.
- **Foundry Local and the offline path have no Azure identity at all.** Nothing in this lens applies to
  them.
- **A tenant given as a domain rather than a GUID is valid.**
  `AzureServicePrincipalValidator.LooksLikeTenant` (`:82-105`) accepts `contoso.onmicrosoft.com`
  because Entra does. Do not ask for a GUID-only check. The client id is correctly strict.
- **Pre-existing code the diff did not introduce.** If the shape was already there and unchanged, it is
  not this change's finding.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:azure-credential findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
