# Work in progress — handover

Status of the campaign-safety work as of 2026-10-03, for whoever picks it up next. The README
describes the design; this file covers where things stand, what was decided and by whom, and
what is still open. Delete it once the open items have homes elsewhere.

- **Branch:** `main`. This is a single-person repo: work is integrated on `main` locally and pushed, with no PRs (owner, 2026-10-03). The old `claude/email-campaign-safety-system-lr7q1m` branch was fast-forwarded into `main`.

## Background

Neelam Aesthetics sends marketing email through **Square Marketing**, which has no API for
campaigns. The one-year / "Beauty Bank" email went out **twice** with mistakes:

1. **First send:** both membership offers were identical.
2. **Second send:** one offer's contents were updated, but both were still named "Platinum Member".

The original also had wording errors ("50% Complimentary Wellness Injections", "Hi Beautiful🤍",
the same sentence three times, no join link, no terms). Both sends are reproduced as fixtures
in `tests/Neelam.Campaigns.Tests/SampleCampaigns.cs`, and the README's "Why" table maps each
mistake to the check that now stops it.

The owner's stated goals: no duplicate offers, and no embarrassing mistakes "that a simple LLM
could catch".

## Decided by the owner

Treat these as fixed unless the owner reopens them.

- The sending platform is **Square Marketing**, not Squarespace.
- The stack is **ASP.NET / Blazor Server on Azure App Service**.
- Storage is **Azure Storage only**, with no SQL or other database. Extended on 2026-10-03: **Table Storage** in the same account holds metadata (clients, support grants, approvals and dismissals), reached with the managed identity like the blobs. Saves are never indexed there.
- Infrastructure is written in **Bicep**.
- **Managed identity only.** No account keys, SAS or client secrets, anywhere; the resource group denies key authentication through Azure Policy (see `infra/main.bicep`). Only the server matters; local runs need not prove it.
- **Connection strings: fine, but never in GitHub** (2026-10-02, replacing 2026-10-01's "no connection strings at all"). The Application Insights connection string is acceptable. The general case is not a concern; a connection string just has to be stored where it would not be expected to be passed on, such as an App Service setting, and must not appear in any GitHub data. `CredentialGuard` now refuses only secrets (keys, SAS, passwords); `RepositoryTests` scans every file git would commit for a real-looking key. Git history was checked by hand on 2026-10-02 and holds only placeholders.
- **Saves are date/time-stamped blobs.** Any blob can be deleted with no remaining record, so there is no index, versioning, soft delete or logging that would keep one. Clarified on 2026-10-02: the rule is about not keeping blob history, so a deleted save's contents cannot be recovered. Telemetry that names a blob is fine, because it never holds the blob's data, and customer data does not live in blob names or metadata.
- **Clients and access** (2026-10-03; the README's "Clients and access" section is the design):
  - One deployment serves every client. The tool is for people who cannot set this up themselves, so the operator onboards and runs each client; clients' people only sign in.
  - Separation between clients is enforced **in code**, from Entra alone (owner, 2026-10-03: "everything should be Entra based"). Each client has an Entra security group the operator manages, and the sign-in token says which groups a user is in. **Operator** is an app role, which the operator can give to anyone helping them, with the same limits. No membership or role lives in a table or a setting. This replaces the per-deployment `Storage:Client` / `clientName` from commit `7141d54`, which the rework removes.
  - One container per client, named by `ClientName`, with the same layout for every client.
  - The operator role covers clients, members and settings, **not** campaign data. Reading a client's drafts and templates takes a **support grant, which the client grants** in the app, with a reason and an expiry. The row stays as the record.
  - **Exception until 1.0:** the operator works with Neelam's data directly while the tool is shaped with Neelam. This is a standing support grant marked until 1.0, removed at 1.0.
  - **The catalog is the client's:** the procedures and medications to offer, and their prices, live in the client's own container (`{client}/catalog/{stamp}.json`). The operator does not need them, and sees them only under a support grant (refined 2026-10-03).
  - **Check policy is the client's too:** restricted terms, medical terms and the emoji limit are client decisions. Each client sees and controls its own policy, at `{client}/policy/{stamp}.json` in its own container, loosening included. The operator sees it only under a grant (decided 2026-10-03).
  - **Each client has its own look**, at `settings/{client}/{stamp}.json` in a `settings` container. That client's members and the operator can both read and edit it; other clients cannot see it. Timestamped like saves.
  - **Development** runs against a separate test deployment with no client data; production drops `developerPrincipalId`.
- **Security is declared now and enforced at rollout** (2026-10-03). Every page and endpoint carries its real `[Authorize(Policy = ...)]` attribute from the start, and a contract test fails the build if one is missing. While prototyping, the policies are registered permissively in one place; at rollout (before 1.0) that one registration changes to "signed in and holding the Entra app role", and every attribute takes effect at once. This keeps the Entra setup (app registration, app roles, client groups) from blocking all other work. Two conditions: the **tests run with the enforcing policies from day one**, so the attributes are proven continuously; and permissive mode runs only where there is **no real client data** (local, and the test deployment), never on a deployment holding Neelam's data.
- Campaigns are created **from templates**. Refined 2026-10-03 under the owner's principle **"data over dogma"**: the campaign model is hard-coded to Neelam's membership email (fixed sections; one `Offer` of `Tier`s with `MonthlyPrice`; Beauty Bank's benefit kinds), so templates are to define structure as ordered **blocks**, with rules attached to block types and the typed tiered offer kept as one block type. The block vocabulary is to be derived from real client emails, not an assumed list. Decided the same day: no older campaigns exist yet, so it is **best effort from a sample of one** (the Beauty Bank email), refined as examples arrive. **Templates are the client's own data**, in its container: clients can build their own (with the ability, if not yet the competence), and the operator does **breakdowns on request**, under the client's support grant. Built the same day; one judgment call made with it, the owner's to overrule: "an email needs a button" (`cta-required`) now applies only to an email with an offer, which was the Beauty Bank failure; other templates mark their buttons required or not.
- **Tier copy must not merely copy everything.** It leaves the name and price blank and marks copied benefits unreviewed.
- An **AI proofread** is part of the gate.

## Built (on `main`)

| Area | Where | State |
|---|---|---|
| Campaign model, typed benefits | `src/Neelam.Campaigns` | Done, tested. Since 2026-10-03 a campaign is a subject, a preheader and **template-defined blocks** (heading, greeting, paragraphs, offer, button, sign-off, fine print), derived from the Beauty Bank email as a sample of one; rules attach to block types. The membership email is now one template among others |
| Rule checks (`CampaignReview`) | `src/Neelam.Campaigns` | Done, tested |
| Gate + dismissals (`CampaignGate`) | `src/Neelam.Campaigns` | Done, tested with a fake proofreader |
| Templates, drafts, slot origins, tier copy | `src/Neelam.Campaigns` | Done, tested |
| Draft/template JSON (`CampaignJson`) | `src/Neelam.Campaigns` | Done, tested; schema 2 since the block model (nothing was ever saved as 1) |
| Export to editor blocks (`EditorExport`) | `src/Neelam.Campaigns` | Done; block mapping to Square **unverified** |
| Claude proofreader | `src/Neelam.Campaigns.Claude` | Compiles; parsing tested; **never called live**. Its instructions no longer assume "a medical aesthetics clinic" (2026-10-03); **open:** where each client's own description of its business lives, to give the proofread that context back as data |
| Blob store (`CampaignStore`, `AzureBlobBackend`) | `src/Neelam.Campaigns.Storage` | Tested in-memory; checked once by hand against Azurite. Reworked 2026-10-03 (step 1 of the rework). Bicep provisions a container per client from a `clients` list in `infra/main.bicepparam` (and `test.bicepparam`), the `settings` container, and the four tables. The app's identity alone holds data roles, each scoped to a container or table; there is no `developerPrincipalId`. The app opens stores per client through `ClientStores`; `ClientName` refuses `settings`. Step 4 (2026-10-03): each client's catalog (`catalog/`) and check policy (`policy/`) live in its container, and its look in `settings/{client}/`, all as `DocumentStore<T>`: timestamped versions, newest in force, delete the newest to undo. `ClientStores.PolicyInForceAsync` gives the client's own policy, or the starting defaults until it saves one, for `CampaignReview` / `CampaignGate`. `ClientLook` has only an accent colour so far |
| Metadata tables, caller from sign-in (`TableMetadata`, `CallerClaims`) | `src/Neelam.Campaigns.Storage` | Row mapping and claim mapping tested; On the test deployment the app reads the clients table with its own identity (`/healthz`); **never run against a real sign-in token**. Clients (name, Entra group, display name) and support grants (kept after expiry as the record) behind `IClientDirectory` / `ISupportGrantStore`. `CallerClaims` turns the sign-in into a `Caller`: groups map to clients through the clients table, and the Operator app role makes the operator |
| Shared packages | `Janet.Azure.Storage`, `Janet.Entra` 0.4.0 (from `..\Janet.Shared`) | Since 2026-10-03, every blob and table client comes from one `StorageClients` built at startup: the app's credential, a shared retry budget, and https-only endpoints with no SAS. Reading the sign-in is `EntraClaims`; Neelam keeps only what groups and roles mean. ImageSelectorV2 uses the same storage package (its commit a4730c6, on its staging slot since 2026-10-03, where its table calls succeed) |
| Access check (`AccessCheck`, `SupportGrant`) | `src/Neelam.Campaigns.Storage/Access.cs` | Done, tested; pure, nothing wired to it yet. Members reach only their own client. The operator manages clients and works on any client's look, but reaches client data only under an active grant from that client; a standing grant (no expiry) covers Neelam until 1.0. Only a member of a client can give its grant. Nobody else reaches anything |
| Startup credential guard | `src/Neelam.Campaigns.Storage` | Done, tested |
| Web host | `src/Neelam.Web` | Skeleton only: wiring + guard, **no campaign pages** |
| Monitoring | `infra/main.bicep`, `src/Neelam.Web/Program.cs` | Application Insights over a Log Analytics workspace, both with local auth off. The site's identity has Monitoring Metrics Publisher, and Bicep fills `APPLICATIONINSIGHTS_CONNECTION_STRING` from the resource. The app uses the Azure Monitor distro with the same credential as storage, only when that setting is present. Default telemetry, blob dependencies included (decided 2026-10-02). **Live on the test deployment since 2026-10-03:** requests and traces arrive in Application Insights with local auth off, so the managed identity signs the telemetry |
| Infrastructure | `infra/main.bicep` | Builds and lints; settings pinned by tests. **Deployed once, as the test deployment** (2026-10-03, `neelam-test-rg`): shared keys off, the three containers and three tables, every data role held by the site and scoped to one container or table. Production not yet deployed |
| Key-auth policy | `infra/main.bicep` | 13 built-in policies assigned with Deny at resource-group scope, 4 App Service ones with AuditIfNotExists (no built-in Deny exists for basic publishing credentials). GUIDs read from Azure/azure-policy on 2026-10-01. **Never deployed**; deploying needs Owner or Resource Policy Contributor. On 2026-10-02 the same 17 were also assigned by hand at **subscription** scope (names `deny-`/`audit-` plus the first 8 characters of the definition id), so the resource-group copies in this template are now redundant but harmless |

## Open decisions (the owner's to make)

Do not settle these on the owner's behalf. Bring options with a recommendation.

1. **Square editor details.** The owner does not know yet:
   - whether Square offers a first-name placeholder, and its exact form
   - whether bullet lists survive pasting into a text block. If they don't, export each benefit as its own line with 🤍 in front.
2. **Sign-in.** The app must not show or change campaigns until Entra ID sign-in exists, or anyone with the URL could read and delete saves.
   - Staying secret-free means a federated credential on the app's identity instead of a client secret.
   - Open: App Service built-in auth or Microsoft.Identity.Web? Either can put the group and app-role claims in the token.
   - Open: how clients' people get accounts with no setup of their own. Guests invited into the operator's directory, or Microsoft Entra External ID (an emailed code or an existing account)? External ID is likely kinder for non-technical users, but it is a separate setup.
3. **Approval.** Is one approver enough, or must a second person approve before export? Must warnings be acknowledged individually, or do only blockers stop export (the current behaviour)?
4. **Claude.** No answer yet. Options: call the Anthropic API directly, or go through Microsoft Foundry in the Azure subscription (`AnthropicFoundryClient`, same proofreader code).
   - An API key, or another way to authenticate, is needed before the proofreader can be run on the two real sends.
   - Server-side refusal fallbacks were left out because they aren't available on Foundry. A refusal currently blocks the email until a person dismisses it.
5. **Placeholders the owner should replace:**
   - the "Gold Member" tier name and the `example.com` join and terms links in the fixtures
   - the restricted terms, medical terms and emoji limit in `CampaignPolicy`. These become Neelam's own policy, which Neelam sets.
   - whether "Beauty Bank" and "savings account" are acceptable. They are warnings, not blockers, on purpose: it is a business and legal call.
6. **"Start from last campaign".** Offered but not answered. It would reuse slot origins, so every copied field has to be edited or confirmed.
7. **Copied benefit amounts.** Offered but not answered: should a copied tier's amounts start blank, so every number has to be re-typed? Currently each copied benefit is kept but marked unreviewed.

## Suggested next steps, once the decisions above land

1. Rework `7141d54` to the clients-and-access design, before sign-in exists:
   - **Bicep:** a `clients` list, with a container and the app's blob role per client; the `settings` container; Table Storage with *Storage Table Data Contributor* for the app; no `clientName`, no `developerPrincipalId`.
   - **Code:** the access check as a pure, tested component (member, operator, support grant, the Neelam-until-1.0 grant); stores opened per client; the catalog and check policy (client container) and the look (`settings` container), with their stores, the policy feeding `CampaignReview`; `ClientName` refusing reserved names such as `settings`.
2. Sign-in, then the first real pages: list saves, open a draft, save, delete.
3. A draft editor in Blazor, built on `CampaignDraft` / `Slot`. Show each slot's origin and a "confirm" action for copied values.
4. A review screen: findings, dismissals with name and reason, and the export blocks with copy buttons.
5. Persist dismissals and approvals in Table Storage (decided 2026-10-03).
6. First real deployment of `infra/main.bicep`, plus a separate test deployment for development.
7. A live proofread of `FirstSend()` and `SecondSend()` once Claude access is decided.

## Environment notes

- **.NET:**
  - Targets `net10.0` (moved from `net8.0` on 2026-10-02); App Service runs `DOTNETCORE|10.0`. Sessions are local with SDK 10.
  - The solution is still a classic `Neelam.sln`, a leftover of the SDK 8 days; `.slnx` would now build.
  - Janet.Azure.Storage and Janet.Entra come from the local feed `..\.packages` (`nuget.config`), packed from `..\Janet.Shared` with `dotnet pack Janet.Shared.slnx -c Release -o ..\.packages`. A restore without that feed fails with NU1301.
  - Tests use xunit 2.4.2. Use `Assert.Equal` on sorted arrays, not `HashSet`s, for order-free comparisons.
- **Bicep:** Bicep CLI 0.47.16 and Azure CLI 2.90.0 installed with winget on 2026-10-02; `az` uses the PATH `bicep`. Signed in with `az login` on 2026-10-02 (the tenants need MFA, so sign in with `--tenant`).
- **Test deployment** (created 2026-10-03): resource group `neelam-test-rg` (westus3), site `https://neelamtest-6excvnu62r4z6.azurewebsites.net`, storage account `neelamtest6excvnu62r4z6`, clients `test-salon-one` and `test-salon-two`, environment `Test` (so the prototype's permissive access may run there, and nowhere else). Redeploy the template with `az deployment group create -g neelam-test-rg -f infra/main.bicep -p infra/test.bicepparam`; deploy the app with `& "$env:JanetBase\scripts\Invoke-BuildDeploy.ps1" -ManifestPath .\deploy-manifest.test.json`, which verifies `/healthz` (the app reading its clients table with its own identity). Costs a B1 plan plus a little storage and monitoring.
- **Azurite check (manual):**
  1. `npm i azurite@3`, then make a self-signed certificate for `127.0.0.1`.
  2. Run `npx azurite-blob --oauth basic --cert cert.pem --key key.pem`.
  3. Point a probe at `https://127.0.0.1:10000/devstoreaccount1`, with `SSL_CERT_FILE=cert.pem` and `NO_PROXY=127.0.0.1`.
  4. "Basic" OAuth mode accepts an unsigned JWT with `aud=https://storage.azure.com`, an `sts.windows.net` issuer and a valid expiry.
  5. Stop it with `pgrep -f "node.*azurite"` + `kill`, not `pkill -f azurite`: that pattern also matches the shell running it.
- No test touches the network. Keep it that way, and keep real-service checks as manual probes.
