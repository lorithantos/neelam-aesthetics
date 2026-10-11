# Moving the Neelam site onto the Windows P1mv3 plan, under a name without "test"

Planning only. Nothing in Azure was changed, nothing in the repo was edited, nothing was committed.
Verified 2026-10-10 (Saturday evening, PT) with read-only `az` / ARM / Log Analytics calls.

> **Since this plan was written (owner, 2026-10-11; WIP open decision 13 is the current record):**
> D1 name **`campaign-check`**; D3 option **A**, a new resource group of its own using the P1mv3 plan;
> D4 option **(c)**, recreate and re-seed; D6 **keep staging**. **Order:** the move waits for the
> client's feedback on the test site, and the **security lock-down comes before the move** (no secret
> in the app's settings, likely Workload Identity Federation; the site locked to her), so the new site
> opens with the real flow from day one -- which likely makes D5 (key) moot. Section 0's numbers and
> the "2026-10-10 22:00" gate are as of that date; re-check before acting. The re-seed notes the
> runbook cites (`scratchpad/...`) lived in that session's temporary scratchpad (step R2).

---

## 0. What is there now (verified)

| | Neelam today | Target plan |
|---|---|---|
| Plan | `neelamtest-plan`, **B1 Linux**, West US 3, webspace `neelam-test-rg-WestUS3webspace-Linux`, 2 sites | `ImageSelectorTest20240604205112Plan`, **P1mv3 Windows** (`reserved: false`), **West US** (stamp `waws-prod-bay-227`), webspace `Lori_Photography-WestUSwebspace`, 1 worker, 2 sites |
| Sites | `neelamtest-6excvnu62r4z6` (client, `test-salon-one`), `neelamtest-staging-6excvnu62r4z6`; both `app,linux`, `DOTNETCORE|10.0`, Always On, WebSockets on, ARR affinity on, HTTP/2, TLS 1.2, FTPS off, system identity, easy auth off, no custom domains | `LtImageSelector` (+ `staging` slot; Always On off, `v10.0`, 64-bit, WebSockets off) and `LtRetirement` (Always On on, `v10.0`, **32-bit**, 0 requests in 7 days) |
| Resource group | `neelam-test-rg`, location westus3 | `Lori_Photography`, **location westus3** (the plan and sites in it are West US; its storage `imageselectorstorage` is West US 3) |
| Data | `neelamtest6excvnu62r4z6` (containers `settings`, `test-salon-one`, `test-salon-two`; tables `activity`, `approvals`, `clients`, `knownItems`, `supportGrants`), `neelamteststg6excvnu62r4` (staging). Both: shared key off, OAuth default, no public blob access, TLS 1.2; versioning, blob/container soft delete, change feed, restore all off; no diagnostic settings; no lifecycle policy | |
| Vault | `neelamtest-kv-6excvnu62r`; the `Proofread__AnthropicApiKey` reference shows **Resolved** on both sites (so the key is set) | |
| Monitoring | `neelamtest-insights-6excvnu62r4z6` over `neelamtest-logs-6excvnu62r4z6`, West US 3 | |
| Policy | 17 assignments at `neelam-test-rg` (13 Deny, 4 AuditIfNotExists) **and the same 17 at subscription scope** (`deny-*`/`audit-*`), so they already cover `Lori_Photography`. (WIP's Built table still says the RG copies were "never deployed"; they are deployed.) | none at RG scope; the subscription's 17 apply |
| RBAC | Each site identity: Blob Data Contributor per container, Table Data Contributor per table, Monitoring Metrics Publisher on insights, Key Vault Secrets User on the vault. The owner (as developer): Blob + Table Data Contributor on both accounts, Secrets Officer on the vault | At RG scope only the owner (Storage Account Encryption Scope Contributor). Resource-scoped: LtImageSelector's identities and a user-assigned deploy identity (`staging-id-9baf`, Website Contributor on LtImageSelector and its slot), and **one guest account** with Storage Account Contributor + data roles on `imageselectorstorage` only. Nothing at RG scope reaches new resources except the owner's role |
| Entra | No app registration names Neelam (7 registrations, none with a Neelam redirect URI). Sign-in is not built, so no redirect URI depends on the old name | |

**Capacity of the target plan, last 7 days (2026-10-03..10, hourly):** CPU mean 0.3 %, p95 of hourly averages 0.7 %, highest 1-minute peak 82 %; memory mean 18.5 % of 16 GB (about 3 GB), never above 24 %. Neelam's two sites use a working set of 129 MB mean / 222 MB peak (client) and 18 / 228 MB (staging). **For comparison, the B1 Linux plan runs at 75 % memory on average and peaked at 96 %** (1.75 GB): the move also takes Neelam off a plan that is short of memory.

**.NET 10 on Windows:** `az webapp list-runtimes --os windows` lists `dotnet|10` (10.0 LTS, supported to 2028-11-14), and both apps on the target plan already run `netFrameworkVersion: v10.0`.

**Traffic and storage use (Application Insights, 7 days, counts only, no content read):** per first page load (prerender) the client site makes on average 4.7 storage calls for `/campaigns` (max 9), 7.1 for a campaign (max 13), 3.5 for `/known-items` and `/templates`, 3.4 for `/images`; the interactive start of a page (`UpdateRootComponents`) adds 2.3-2.7 on average, up to 11-13. Same-region call latency today: p50 8.9 ms blob, 9.4 ms table.

---

## 1. Can the existing web app be moved to that plan?

**No.** Microsoft Learn: "You can move an app to another App Service plan, as long as the source plan and the target plan are in the same resource group and geographical region and of the same OS type. Any change in type, such as Windows to Linux ..., isn't supported." Plans live in a *webspace* fixed at creation per resource group + region + OS, and "plans can't move webspaces after they're created" ([Manage an App Service plan: Move an app to another plan](https://learn.microsoft.com/azure/app-service/app-service-plan-manage#move-an-app-to-another-app-service-plan)). Neelam fails all three: other RG (`neelam-test-rg-WestUS3webspace-Linux` vs `Lori_Photography-WestUSwebspace`), other region (West US 3 vs West US), other OS. Clone App does not exist for Linux. **So both sites must be recreated** on the Windows plan (new site resources, so new identities, new role assignments, new settings, a fresh deploy).

**Where the new sites can live.** The webspace rule constrains which *plan* an app can use, not which resource group the *site resource* sits in. A site may sit in a different RG from its plan: Learn's move rules describe apps that are in one RG while "hosted" in their original one ([Move App Service resources](https://learn.microsoft.com/azure/azure-resource-manager/management/move-limitations/app-service-move-limitations): "You can only move App Service resources from the resource group where you originally created them"), and ARM accepts a `serverFarmId` in another RG of the same subscription. Learn does not state the create case outright, so **confirm it with a what-if / validation before relying on it** (step R5). Consequences if the sites sit in a Neelam RG but use the Lori_Photography plan:
- the sites are hosted in `Lori_Photography-WestUSwebspace`; they can only ever move to another plan in that webspace, and a later cross-subscription move must move them with Lori_Photography's App Service resources ("You must move all App Service resources in the resource group together");
- the site's region must be the plan's (West US); everything else (storage, vault, insights) can be anywhere.

**Options:**
- **A. New Neelam resource group (recommended)**, e.g. `neelam-campaigns-rg` (West US), holding the two sites, storage, vault and monitoring; only the plan is in `Lori_Photography`. Keeps Neelam's RBAC, policy, deployment history and clean-up separate from the photography project.
- **B. Everything in `Lori_Photography`.** Simplest Bicep (plan in the same RG). But any future RG-scope grant on the photography project (helper, deploy identity) would reach the client's sites and data; the template's 17 RG policy assignments would land on the photography RG (no behaviour change, the subscription already has them, but untidy); `uniqueString(resourceGroup().id)` names change anyway.
- **C. Keep `neelam-test-rg`** for the new sites (RG location does not matter). Works, but keeps "test" in the RG name and mixes old and new until the old is gone.

---

## 2. Linux -> Windows: what changes

### Bicep (`infra/`)
- **`main.bicep`**: the plan becomes an existing resource in another RG when one is named, else created as now (so production keeps a path to its own plan):
  `param planId string = ''` (or `planName` + `planResourceGroup`); `resource plan ... existing = { name: ..., scope: resourceGroup(planResourceGroup) }`. The `kind: 'linux'` / `reserved: true` plan stays only on the create path (or the OS becomes a parameter; open decision D7).
- **Location**: today `location = resourceGroup().location` for everything. The sites need `siteLocation = 'westus'` (the plan's region), separately from the data's location.
- **`site.bicep`**: `kind: 'app,linux'` -> `kind: 'app'`; drop `linuxFxVersion: 'DOTNETCORE|10.0'`; add `netFrameworkVersion: 'v10.0'`, `use32BitWorkerProcess: false` (a new Windows site can default to 32-bit, as `LtRetirement` shows), and `metadata: [{ name: 'CURRENT_STACK', value: 'dotnet' }]` (the portal's stack marker; optional). Keep `webSocketsEnabled: true` (on Windows this is what lets Blazor's circuit use WebSockets rather than falling back), `alwaysOn: true` (the undo sweep's timer needs a running process), `clientAffinityEnabled: true`, `http20Enabled`, `minTlsVersion`, `ftpsState`, `httpsOnly`, `keyVaultReferenceIdentity`, the two basic-publishing-credentials resources. No startup command exists on Linux today, so nothing to translate.
- **Recommended new app setting: `WEBSITE_RUN_FROM_PACKAGE = 1`.** On Windows, files in `wwwroot` are locked while the app runs ([App Offline file: Locked deployment files](https://learn.microsoft.com/aspnet/core/host-and-deploy/app-offline)); with run-from-package, `config-zip` uploads the zip to `D:\home\data\SitePackages` and mounts it read-only ([Run from package](https://learn.microsoft.com/azure/app-service/deploy-run-package#run-the-zip-package)), so a deploy is atomic and cannot half-overwrite DLLs. The app writes nothing to `wwwroot`. CredentialGuard sees "1", not a secret.
- **Names** (section 5): an explicit name per resource (param object in the `.bicepparam`) replaces `${prefix}-${suffix}`, since `prefix` allows only 3-11 letters and the suffix is the random-looking part.
- **`vault.bicep`, `storage.bicep`**: unchanged in content; new instances.

### Deploy manifests
`deploy-manifest.test.json` / `deploy-manifest.staging.json`: new `app`, `resourceGroup`, `verify.url`. **Package step unchanged**: `dotnet publish` with no RID is framework-dependent and portable, and the Web SDK already writes `web.config` for IIS/ANCM (in-process by default). Janet's push is classic zipdeploy (`az webapp deployment source config-zip`, `Invoke-BuildDeploy.ps1` line 247), which works on Windows and with basic auth off (Entra token). The build stamp sink (`LATEST_BUILD_INFO`) follows the manifest's `app`. The "Activate" restart after the stamp is unchanged.

### App behaviour
- **Paths**: the catalog, glossary and proofread instructions load with `Path.Combine(AppContext...)` (`BlockCatalog.Shipped`, `ProofreadInstructions.Shipped`): portable. No literal `/home` or `D:\` in the graph.
- **Case sensitivity**: Windows is case-insensitive, so Linux->Windows only loosens; blob names are case-sensitive in Azure either way.
- **`HOME`**: `D:\home` instead of `/home`; nothing in the app reads it.
- **Data Protection keys**: the app configures none (no `DataProtection` node in the graph), so on App Service the keys go to `%HOME%\ASP.NET\DataProtection-Keys`, per site, not encrypted at rest, on Windows as on Linux ([Data Protection default settings](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/default-settings?view=aspnetcore-10.0); [Deploy ASP.NET Core to App Service: key ring](https://learn.microsoft.com/aspnet/core/host-and-deploy/azure-apps/?view=aspnetcore-10.0#data-protection-key-ring-and-deployment-slots)). New site = new key ring; nothing persisted depends on it (no cookies auth yet), only open circuits, which a new URL starts afresh anyway.
- **Time zones**: `LocalTime` uses `TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles")` and builds "PDT" from the zone's daylight name. On Windows .NET resolves IANA ids through ICU and the names come from Windows ("Pacific Daylight Time"), so "PDT" holds. **The test suite already runs on Windows** (the developer's machine), so this path is exercised every build; App Service Windows is the only new ground. Check one page's time on staging (step R8).
- **WebSockets, ARR affinity**: set in the template, as above. Single instance, so affinity changes nothing today.
- **CredentialGuard**: App Service on Windows also exposes each setting a second time with the `APPSETTING_` prefix; the guard's 2026-10-10 fix already accepts exactly that copy (`AppServiceStartupTests` starts the app with both). `WEBSITE_SITE_NAME` is set on both OSes.
- **Telemetry**: the app uses the Azure Monitor distro in code; do not enable the Windows Application Insights site extension (`ApplicationInsightsAgent_EXTENSION_VERSION`), or telemetry doubles.

### Tests that pin what changes (InfrastructureTests; RazorGraph `neelam`, built 2026-10-11T04:13Z)
No test pins Linux: the graph holds no literal containing `linux`, `DOTNETCORE` or `/home`. Tests that need a deliberate edit:
- `The_client_site_and_account_keep_their_names` pins `'${prefix}-${suffix}'`, `'${prefix}stg${suffix}'`, `'${prefix}-staging-${suffix}'` -> pin the new explicit names instead.
- `The_staging_deploy_mirrors_the_client_site_deploy` requires the staging app to be the client app with `-staging` inserted **after the first dash** (`neelam-campaigns` -> `neelam-staging-campaigns`) and fails on a name with no dash (`IndexOf('-')` is -1). Either name accordingly or change the rule.
- `Each_site_has_its_own_account_and_identity` pins `planId = plan.id` and `location = location` for both sites -> `siteLocation`.
- `The_site_gets_the_key_as_a_key_vault_reference_alone` pins `var vaultName = take('${prefix}-kv-${suffix}', 24)` -> the explicit vault name.
- `Policy_refuses_key_authentication` (unchanged if the RG assignments stay in the template; they should).
- **New pins to add**: `kind: 'app'`, `netFrameworkVersion: 'v10.0'`, `use32BitWorkerProcess: false`, `webSocketsEnabled: true`, `alwaysOn: true`, no `linuxFxVersion`, the plan reference, `WEBSITE_RUN_FROM_PACKAGE`.
- Cosmetic only: `CredentialGuardTests` uses `neelamtest-abc` as a sample site name; `InfrastructureTests` line 175 uses the old vault URI as a sample of a valid reference. Both still pass.

---

## 3. Region (plan West US; Neelam's data West US 3)

Microsoft's published round trip West US <-> West US 3 is **19 ms** ([Azure network round-trip latency statistics](https://learn.microsoft.com/azure/networking/azure-network-latency)).

- **(a) Keep the data in West US 3.** Each storage call goes from ~9 ms to ~28 ms. From the telemetry above: a campaign page's first render (avg 7, max 13 calls) and its interactive start (avg 2-3, max 13): if the calls are sequential, **about +0.2 s on an average campaign page, up to +0.5 s at the worst seen**; the campaigns list about +0.1-0.2 s. Cross-region transfer is billed per GB but trivial: Lori_Photography already runs this way (West US app, West US 3 storage) and its "Inter-Region" line was **$0.0014** for the last 30 days. Keeps the client's data where it is; the site's identity is new, so the data roles must be granted to it on the old account (template change to scope the storage module to the old RG).
- **(b) New storage in West US and copy the data.** Blob copy with Entra ID (`azcopy` / `az storage blob copy` with `--auth-mode login`, no keys or SAS) carries blobs and metadata (including undo marks); **tables have no copy tool**, so a small script would read and write entities with `az storage entity ... --auth-mode login`. The destination has versioning, soft delete and change feed off, so the copy leaves no recoverable copy there; the source keeps everything until it is deleted. Verify by counts and SHA-256 per blob. Rollback: point back to the old account. More work than (c) for content we made ourselves.
- **(c) New empty storage in West US, re-seeded by re-entering what we built (owner's lean, 2026-10-10: "We built the data that's there anyway. We can just recreate it.").** No data copy at all, so no copy tooling, no keys, nothing of the old account's history carried. **Gate satisfied 2026-10-10 ~22:00 PT** (orchestrator, owner's permission, list pages only, names and save dates): every campaign, template, image entry, known item and look save on the client's site is ours (two Beauty Bank campaigns, "Membership announcement", 9 images, Botox/Filler/Wellness Injection and our benefit lines, one look save). The runbook re-checks the list pages just before cutover (step R12).
  What re-seeding loses, all ours: approvals and the "seen at export" keys, any LLM report and remembered photo readings (`proofread/...`; a re-read costs a cent or two per photo), the activity trail, campaign ids. Nothing the client made.

**Recommendation: (c)**, with storage, vault and monitoring in **West US** beside the plan. It removes the latency, is the least work, and never touches the client's account except to keep it. **Keep the old storage account** (not deleted) until the owner says otherwise.

**Re-seed sources** (all public Beauty Bank material or the client's own registration, both allowed on the test system by the owner's 2026-10-09 exception):

| What | Source | How it is entered on the new site |
|---|---|---|
| Client record: `test-salon-one` (or the new client name), display name "Neelam Aesthetics", description, phones, time zone | The old site's pages (display name shows on lists); WIP "Registered phone numbers" ((425) 773-5261, 425-877-8646: owner confirms which); description as now | Clients table written directly with the developer's access (`az storage entity insert --auth-mode login`): the demo answers 404 on `/admin/clients`. Needs approval (data-plane write) |
| Look (one save) | Old site's `/look` (read in the browser) and `scratchpad/neelam-look-proposal.json` (#faf0f2, #fffbf7, #e3cfcf, #2d2525, #6b5858, #225e3e) | `/look`, Save |
| 9 Square images (Back to School header/promo, Book appointment header/photo, Holiday header/photo, Logo, Principals seated/toasting) | Old site's `/images` (name + Square address behind the disclosure) | `/images` -> Add from Square, retyped/pasted |
| Known items (Botox, Filler, Wellness Injection, benefit lines, tiers) and any ladders/baseline she has | Old site's `/known-items`; `scratchpad/known-items-proposal.md` | `/known-items` |
| Template "Membership announcement" | Old site's template page; `BeautyBankEmail` in the repo | `/templates/new` |
| Two campaigns, first and second send, with labels | `scratchpad/v1-reentry.md`, `v2-reentry.md`; the owner's `.eml` files (kept outside the repo); `BeautyBankEmail.FirstSend()/SecondSend()` (`tests/Neelam.Campaigns.Tests/SampleCampaigns.cs`) | `/campaigns/new/{template}`, field by field |

**Everything above is re-typed by hand in a browser** (the orchestrator can read the old pages and drive the new ones), except the client row. The scratchpad notes live under `%TEMP%`, which is not durable: **copy them somewhere kept before the move** (they hold only public email content).

---

## 4. Policy and isolation

- **Policy**: the 17 assignments in `main.bicep` are created at whatever RG the template is deployed to; deploying them needs Owner or Resource Policy Contributor (the owner is subscription Owner). The **subscription-scope copies already cover every RG**, `Lori_Photography` included, so nothing is lost in any option. Keep them in the template (pinned by `Policy_refuses_key_authentication`).
- **Managed identity only**: unchanged. New sites get new system identities; the template grants them their data roles, Metrics Publisher and Secrets User exactly as now. No key, SAS or connection string is introduced. `WEBSITE_RUN_FROM_PACKAGE=1` is not a secret.
- **No secrets in GitHub**: unchanged; the Application Insights connection string is still filled in from the resource at deploy time.
- **"The operator does not read client data"**: enforced in code (Entra groups, app role, support grants). Sharing a plan does not give any other app's identity a data role: each site's identity holds roles only on its own account. Windows App Service runs each site in its own sandboxed process with its own `D:\home`.
- **What placing the client's site in `Lori_Photography` (option B) would weaken**: not a CLAUDE.md rule directly, but the blast radius of RG-scope grants. Today that RG has, besides the owner, a guest account and a deploy identity scoped to photography resources only. Anyone later given Contributor on `Lori_Photography` would control Neelam's sites, and so could deploy code that reads her data through the site's identity. Option A avoids this; only the *plan* is shared, and rights on the plan alone do not reach the site's configuration or data.
- **RBAC changes**: all new, all by the template: per-container/per-table data roles for each new site identity on its own new account; Metrics Publisher on the new insights; Secrets User on the new vault for both sites; the developer's Blob/Table Data Contributor on both new accounts and Secrets Officer on the new vault (test deployment only, as now). Nothing on the old resources changes until retirement. Option (a) would instead need the new identities granted on the old account (cross-RG scope).

---

## 5. Names

Site names are global (`<name>.azurewebsites.net`); storage account names are global, 3-24 lowercase letters and digits; vault names are global, 3-24 letters, digits and hyphens, and a deleted purge-protected vault keeps its name for the retention period (90 days here).

Checked read-only on 2026-10-10 (ARM `checkNameAvailability`, checked against known-taken names, `ltimageselector` and the current site, which it correctly reported taken; plus DNS, none resolve):

| Option | Client site | Staging site (current test rule: `-staging` after first dash) | Storage (client / staging) | Vault |
|---|---|---|---|---|
| 1. **neelam-campaigns** | available | `neelam-staging-campaigns` (pattern) / `neelam-campaigns-staging` available | `neelamcampaigns` / `neelamcampaignsstg` available | `neelam-campaigns-kv` available* |
| 2. **neelam-mailcheck** | available | `neelam-mailcheck-staging` available | `neelammailcheck` / `neelammailcheckstg` available | `neelam-mailcheck-kv` available* |
| 3. **neelam-emails** | available | `neelam-emails-staging` available | `neelamemails` / `neelamemailsstg` available | `neelam-emails-kv` available* |
| 4. **campaign-check** (product name, not the client's) | available | `campaign-check-staging` available | `campaigncheck` / `campaigncheckstg` available | `campaign-check-kv` available* |

\* `az keyvault check-name` also reported the *existing* `neelamtest-kv-6excvnu62r` as available, so treat vault availability as unconfirmed until the deploy. Availability is as of the check; names are first-come.

**Recommendation**: settle first whether this address is the one that lasts through go-live (open decision 2 wants "a site address that does not change at go-live"). If it lasts and the site will serve every client, a product name (option 4) ages better than a client's name on a shared tool; if it is Neelam's demo until a production deployment exists, option 1 (`neelam-campaigns`) is the clearest to her. Staging is not client-facing; it can be anything.

**What depends on the old names** (repo text search, graph, Entra):
- `deploy-manifest.test.json`, `deploy-manifest.staging.json` (app, RG, verify URL) and the test that pins their relation;
- `infra/test.bicepparam` (`prefix = 'neelamtest'`), and the name pins in `InfrastructureTests`;
- `WIP.md` (6 mentions: environment notes, key set/rotate commands with the vault and site names, the deploy command), `README.md` lines 359-360 (also stale: says `westus2`), `CLAUDE.md` none;
- `LATEST_BUILD_INFO` lives on each site; the new sites get it on their first deploy;
- How it works and other pages carry no absolute URL; no Entra app registration or redirect URI references Neelam (sign-in not built); no custom domain.
- **For the client**: a new URL to bookmark. Nothing else of hers depends on the address.

---

## 6. Cost

- **Saves**: B1 Linux, West US 3, **$0.017/hour = $12.41 per 730-hour month** (Azure Retail Prices API, "Azure App Service Basic Plan - Linux", B1; [App Service pricing](https://azure.microsoft.com/pricing/details/app-service/linux/)). Cost Management agrees: $2.72 for `neelam-test-rg`'s plan since 2026-10-03. **Only once the B1 plan is deleted**: stopped apps and empty plans still bill ([Plan and manage costs for App Service](https://learn.microsoft.com/azure/app-service/overview-manage-costs#understand-the-full-billing-model); [Delete an App Service plan](https://learn.microsoft.com/azure/app-service/app-service-plan-manage)).
- **Adds**: nothing for compute: the P1mv3 plan bills per instance, not per app ([App Service plans: cost](https://learn.microsoft.com/azure/app-service/overview-hosting-plans#cost-of-app-service-plans)); it already costs ~$96.74 for 30 days on this subscription's rate (Windows P1mv3 list price is $0.37/hour). Storage, vault and Log Analytics are recreated at the same tiny scale (under $1/month today). Option (a) only: cross-region transfer, about a tenth of a cent a month at today's volume. During the overlap both plans bill, a few dollars at most.
- Net: about **$12/month** off the Visual Studio credit. The bigger gain is headroom (16 GB vs a B1 at 75-96 % memory).

---

## 7. Runbook (who does what; downtime)

Roles: **Owner** = anything with keys, the Anthropic console, `az login`, messages to the client, and every approval. **Orchestrator** = repo changes, `bicep_check`, `azure_whatif`, `azure_deploy`, `az` changes and browser work, each Azure change only with the owner's approval.

| # | Step | Who | Verify |
|---|---|---|---|
| R1 | Owner decides D1-D8 (below) | Owner | |
| R2 | Copy the re-seed notes (`v1-reentry.md`, `v2-reentry.md`, `known-items-proposal.md`, `neelam-look-proposal.json`) out of `%TEMP%` to somewhere kept | Orchestrator | files present |
| R3 | Repo change: Bicep (plan reference, `siteLocation`, Windows site config, explicit names, `WEBSITE_RUN_FROM_PACKAGE`), new or edited `.bicepparam`, manifests, InfrastructureTests (edits above + new pins); commit on `main` | Orchestrator (agent) | `janet check` green; `bicep_check` on `test.bicepparam` and `main.bicepparam` (and the new param file) |
| R4 | `az login` if needed; create the new RG (option A), West US | Owner (or orchestrator with approval) | `az group show` |
| R5 | `azure_whatif` of the new deployment into the new RG | Orchestrator, approved | Creates only; sites with `serverFarmId` = the Lori_Photography plan (this is where a cross-RG plan reference would be refused); no change listed in `Lori_Photography` or `neelam-test-rg`; 17 policy assignments; role assignments as section 4. Report with the `bicep_check` result |
| R6 | `azure_deploy` | Orchestrator, approved | `az webapp show`: `kind: app`, plan id, Always On, WebSockets, 64-bit, `v10.0`; storage settings as today (management plane); role assignments; vault reference shows **not yet resolved** |
| R7 | Owner: create a **new** Anthropic key (leave the old one live), set it in the new vault with the masked command (`Read-Host -MaskInput`, `--query` that leaves the value out, as in WIP "To switch it on"), then refresh both new sites' Key Vault references and restart them | Owner (refresh/restart: orchestrator with approval) | `configreferences/appsettings` shows **Resolved** on both new sites |
| R8 | Deploy the current commit to the **new staging** site (new manifest) | Orchestrator, approved | `/healthz` 200; footer build stamp; no `LogFiles/StartupLogs/*_failure.log`; browser devtools show the `_blazor` connection as **WebSocket**, not long polling; a time shows "PDT"; one proofread on a sample (costs ~$0.05: needs the owner's OK) |
| R9 | Deploy the same commit to the **new client** site | Orchestrator, approved | `/healthz` 200 (the app reads its new clients table) |
| R10 | Seed the new client site: client row (data-plane write, approved), then look, 9 images, known items, template, two campaigns, labels (browser, from the old pages and the notes) | Orchestrator; client row approved by owner | New list pages match the old: same names and counts; each campaign's "What the checks say" gives the same findings as on the old site |
| R11 | Seed the new staging site (as WIP "Seeding": `staging-salon` row, template, photos, known items), if staging is kept | Orchestrator | as R10 |
| R12 | **Just before cutover**: re-check the old site's list pages (campaigns, templates, images, known items, look history) for anything newer than the 2026-10-10 22:00 check; re-enter anything new | Orchestrator, owner's permission (as on 2026-10-10) | nothing newer, or re-entered |
| R13 | **Cutover**: owner messages the client the new URL (e.g. **9:00 AM** the next morning; she is done for the day by 9:45 PM, so R4-R12 fit in the evening) | Owner | |
| R14 | Stop the old client and staging sites (`az webapp stop`, reversible; not deleted) | Orchestrator, approved | old URL shows the stopped page; new site in use (Application Insights) |
| R15 | Watch about a week | Orchestrator | requests and no exceptions on the new site |
| R16 | Retire, at the owner's word: delete the two old sites and the **B1 plan** (the saving starts here); revoke the old Anthropic key in the console, then delete the old vault (it stays soft-deleted, purge-protected, 90 days, holding only the revoked key); delete or keep the old insights/workspace (30-day retention); **keep the old storage account** until the owner says otherwise | Owner decides; orchestrator executes with approval | Cost Management shows no B1 line |
| R17 | Docs: WIP (environment notes, names, vault commands, deploy commands, the RG-policy "never deployed" correction), README lines 359-360, thread items | Orchestrator | |

**Client-visible downtime: none.** The old site keeps running, untouched, until after she has the new URL; the new site is built, seeded and verified beside it. The only interruption is the bookmark change. If R14 is skipped, she could keep using the old site by mistake: so stop it at the time of the message.

**Rollback**:
- Before R13: delete the new RG; nothing of hers changed.
- After R13/R14: `az webapp start` the old sites and tell her the old URL; anything she entered on the new site would be re-entered on the old (small, in the first days). The old data is untouched throughout.
- Key: the old key stays valid until R16, so both sites can proofread throughout.

**Key Vault, re-pointed**: `site.bicep` builds the reference from the vault name, so the new sites point at the new vault automatically, and `vault.bicep` grants their identities Secrets User. Keeping the old vault instead would need the vault module scoped to `neelam-test-rg` and the old vault kept for good; not recommended, since it would tie the new deployment to the RG being retired. **Application Insights**: new resource in West US with the deployment; the connection string comes from it; old telemetry stays readable in the old workspace until R16.

---

## 8. Risks and recommendation

**Risks**
1. **First run on Windows App Service.** Mitigated: the test suite runs on Windows already; staging first (R8) with explicit checks (startup log, WebSockets, time zone abbreviation, proofread).
2. **Cross-RG plan reference** (option A) not stated on Learn: confirmed or refused at R5 what-if. Fallback: option B or C.
3. **Shared plan**: Neelam's uptime now depends on the photography plan. Scaling, restarting or deleting `ImageSelectorTest20240604205112Plan` (its name invites a tidy-up) takes Neelam down; a busy neighbour shares 2 vCPU / 16 GB (today 0.3 % CPU, 18 % memory). And the sites can never move to a plan outside `Lori_Photography-WestUSwebspace` without being recreated again.
4. **Production path**: if production later needs its own plan (isolation, slots, its own SKU), this is a second recreation. The OS of that plan is open (D7).
5. **Re-seed errors**: hand re-entry can differ from the original; verified by matching findings per campaign (R10).
6. **Notes in `%TEMP%`** could be cleaned before the move: R2.
7. **Name taken between now and deploy**: re-check at R5.
8. **Docs drift**: WIP and README name the old resources in commands (key rotation especially); R17.

**Recommendation: move, in one evening window soon, with option A (own RG, plan shared), data option (c), new vault and insights in West US, and a name the owner picks after deciding whether it is the lasting address.** Reasons: nothing of the client's is stored yet (checked), so the rename is cheapest now and gets harder every day she uses it; the B1 plan is short of memory; the move saves about $12/month; and the downtime is nil. Not tonight: the Bicep/test change and a staging run on Windows come first (R3, R8), which is a few hours of work plus the evening's re-entry.

**Runbook: 17 steps; expected client-visible downtime: none** (a new URL in a morning message).

---

## Decisions for the owner

- **D1. Name** (section 5): `neelam-campaigns`, `neelam-mailcheck`, `neelam-emails` or `campaign-check`, or another. Recommendation: decide D2 first.
- **D2. Is this the lasting address** (open decision 2: "a site address that does not change at go-live")? If yes, the name and RG should be production-grade, and the question of whether it stays a "Test"/Prototype deployment under that address goes with it.
- **D3. Where the sites live**: A new Neelam RG (recommended), B `Lori_Photography`, or C `neelam-test-rg`.
- **D4. Data**: (c) recreate and re-seed (owner's lean; recommended), (b) copy, or (a) keep in West US 3. And: keep the old storage account until when.
- **D5. Key**: new vault + a new Anthropic key (recommended, revoke the old after), or the same key re-set in the new vault.
- **D6. Staging**: recreate it beside the client site on the same plan (recommended, the release flow depends on it), or drop it (the P1mv3 plan also supports deployment slots, but a slot shares the app's name and would change the "own account, own identity" design).
- **D7. Production's plan and OS**: also this Windows plan, its own plan, Linux or Windows. Decides whether `main.bicep` keeps a Linux path.
- **D8. Timing and old site**: which evening; stop the old sites at the 9:00 AM message (recommended) or leave them up a few days; when to delete the B1 plan.
