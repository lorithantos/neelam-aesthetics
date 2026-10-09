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
- Storage is **Azure Storage only**, with no SQL or other database. Extended on 2026-10-03: **Table Storage** in the same account holds metadata (clients, support grants, approvals, the activity trail and known items; dismissals are planned there but not built), reached with the managed identity like the blobs. Saves are never indexed there.
- Infrastructure is written in **Bicep**.
- **Managed identity only.** No account keys, SAS or client secrets, anywhere; the resource group denies key authentication through Azure Policy (see `infra/main.bicep`). Only the server matters; local runs need not prove it.
- **Connection strings: fine, but never in GitHub** (2026-10-02, replacing 2026-10-01's "no connection strings at all"). The Application Insights connection string is acceptable. The general case is not a concern; a connection string just has to be stored where it would not be expected to be passed on, such as an App Service setting, and must not appear in any GitHub data. `CredentialGuard` now refuses only secrets (keys, SAS, passwords); `RepositoryTests` scans every file git would commit for a real-looking key. Git history was checked by hand on 2026-10-02 and holds only placeholders.
- **Saves are date/time-stamped blobs.** Any blob can be deleted with no remaining record, so there is no index, versioning, soft delete or logging that would keep one. Clarified on 2026-10-02: the rule is about not keeping blob history, so a deleted save's contents cannot be recovered. Telemetry that names a blob is fine, because it never holds the blob's data, and customer data does not live in blob names or metadata. **Refined by the owner on 2026-10-09:** "We want as much tracking as we can - deletions too are recorded, but not the data." A deleted save's *contents* stay unrecoverable (no versioning, soft delete, change feed or storage logs), but an activity event saying that save X of campaign Y was deleted at time T, with no subject, text or label, is wanted. See "Approvals and the activity trail" below.
- **Undo is forgiving for a day** (owner, 2026-10-09). "Undo last save" sits right under Save, so one misclick deleted a campaign with a single save for good. Undo now marks the save instead: the time it was undone goes in that blob's own metadata (`undone`), with no index or list of undone saves, and every list, history, opening, check and preview leaves it out, so to the client it is gone at once. For the grace period the editor offers **Restore**, which clears the mark, also after leaving the page and coming back (a campaign or template whose every save was undone shows a Restore page at its address). Leaving the page deletes nothing. A background sweep (`UndoSweep`, at startup and then every `Undo:SweepInterval`, hourly) lists each client's blobs, reads the marks, and deletes every save undone longer ago than the grace period; it keeps no state, so a crash part-way is finished by the next run. Hardened after review (2026-10-09): the sweep covers every client in the clients table and, in Prototype mode, `Prototype:Client` too, once, even with no row for it or an unreadable table; each delete is conditional on the blob's ETag as listed (`IBlobBackend.DeleteIfUnchangedAsync`, If-Match), so a save restored between listing and delete survives; one client failing never stops the others. The period is configuration, `Undo:GracePeriod` = `1.00:00:00` in `appsettings.json`, required (the app refuses to start without a positive one); `Undo:SweepInterval` must lie between a millisecond and 49 days, the range its timer can wait, or the app refuses to start. **This is a deliberate, bounded exception to "a deleted save leaves no record":** for the grace period an undone save still exists. Once the sweep has deleted it, nothing of its contents remains, exactly as before; blob soft delete, versioning and change feed stay off. (Since the activity trail, 2026-10-09, a `DeletedBySweep` event and any approval row for that save stay, by id only, and `infra/main.bicep` gained the `activity` and `knownItems` tables; neither holds content.) Undo applies to drafts and templates (`DraftSession`, `TemplateSession`); a client's catalog, policy and look (`DocumentStore`) have no undo page yet and still delete outright.
- **Clients and access** (2026-10-03; the README's "Clients and access" section is the design):
  - One deployment serves every client. The tool is for people who cannot set this up themselves, so the operator onboards and runs each client; clients' people only sign in.
  - Separation between clients is enforced **in code**, from Entra alone (owner, 2026-10-03: "everything should be Entra based"). Each client has an Entra security group the operator manages, and the sign-in token says which groups a user is in. **Operator** is an app role, which the operator can give to anyone helping them, with the same limits. No membership or role lives in a table or a setting. This replaces the per-deployment `Storage:Client` / `clientName` from commit `7141d54`, which the rework removes.
  - One container per client, named by `ClientName`, with the same layout for every client.
  - The operator role covers clients, members and settings, **not** campaign data. Reading a client's drafts and templates takes a **support grant, which the client grants** in the app, with a reason and an expiry. The row stays as the record.
  - **Exception until 1.0:** the operator works with Neelam's data directly while the tool is shaped with Neelam. This is a standing support grant marked until 1.0, removed at 1.0.
  - **The catalog is the client's:** the procedures and medications to offer, and their prices, live in the client's own container (`{client}/catalog/{stamp}.json`). The operator does not need them, and sees them only under a support grant (refined 2026-10-03).
  - **Check policy is the client's too:** restricted terms, medical terms and the emoji limit are client decisions. Each client sees and controls its own policy, at `{client}/policy/{stamp}.json` in its own container, loosening included. The operator sees it only under a grant (decided 2026-10-03).
  - **Each client has its own look**, at `settings/{client}/{stamp}.json` in a `settings` container. That client's members and the operator can both read and edit it; other clients cannot see it. Timestamped like saves.
  - **Development** runs against a separate test deployment with no client data; production drops `developerPrincipalId`. Done 2026-10-08 (owner's call): `infra/test.bicepparam` names the developer, who gets Blob and Table Data Contributor on the whole test account; the template honours it only when `environmentName` is `Test`, and `InfrastructureTests` pins both.
- **Security is declared now and enforced at rollout** (2026-10-03). Every page and endpoint carries its real `[Authorize(Policy = ...)]` attribute from the start, and a contract test fails the build if one is missing. While prototyping, the policies are registered permissively in one place; at rollout (before 1.0) that one registration changes to "signed in and holding the Entra app role", and every attribute takes effect at once. This keeps the Entra setup (app registration, app roles, client groups) from blocking all other work. Two conditions: the **tests run with the enforcing policies from day one**, so the attributes are proven continuously; and permissive mode runs only where there is **no real client data** (local, and the test deployment), never on a deployment holding Neelam's data.
- Campaigns are created **from templates**. Refined 2026-10-03 under the owner's principle **"data over dogma"**: the campaign model is hard-coded to Neelam's membership email (fixed sections; one `Offer` of `Tier`s with `MonthlyPrice`; Beauty Bank's benefit kinds), so templates are to define structure as ordered **blocks**, with rules attached to block types and the typed tiered offer kept as one block type. The block vocabulary is to be derived from real client emails, not an assumed list. Decided the same day: no older campaigns exist yet, so it is **best effort from a sample of one** (the Beauty Bank email), refined as examples arrive. **Templates are the client's own data**, in its container: clients can build their own (with the ability, if not yet the competence), and the operator does **breakdowns on request**, under the client's support grant. Built the same day; one judgment call made with it, the owner's to overrule: "an email needs a button" (`cta-required`) now applies only to an email with an offer, which was the Beauty Bank failure; other templates mark their buttons required or not.
- **Tier copy must not merely copy everything.** It marks copied benefits unreviewed. **Since 2026-10-09 (owner) it keeps the name and the price**, replacing "leaves them blank": changing either tier is easier than being made to invent a name or price, which could push her into reordering tiers. The rules are the safety net: while the tiers share a name (`tier-names-unique`) or the copy costs no more than the tier before it (`tier-prices-increase`) it is a Must fix, which blocks approval and export; both messages are neutral about which tier to change ("Rename either one.", "change either price, or reorder the tiers").
- An **AI proofread** is part of the gate.
- **On the demo, the operator's pages do not exist** (owner, 2026-10-09). Prototype lets everyone through every policy, so `/admin/clients` was open to anyone with its address, who could change a client's display name or phone numbers. In Prototype mode every page or endpoint that names the `Operator` policy answers a plain 404, the same as an address with no page, before authorization or the page runs (`OperatorPagesInDemo`, in `Program` before `UseAuthorization`). It is decided from each endpoint's policy, not its address, so a new operator page is covered when it is written; a test holds every endpoint under `/admin` to the Operator policy, and no client page links to `/admin`. Today it covers `/admin/clients` only, the one operator page. Enforced is unchanged: the Operator app role decides. So the Clients page cannot be used on the test site while it runs the demo: onboarding or editing test-salon-one's row (display name, phones) there needs another route, such as the table written directly.
- **The test site is a full demo** (owner, 2026-10-09). The client (Neelam, through Priya) can walk every step there: template, campaign, checks, approve, export. It has exactly **two limits**: what she saves is kept on the operator's test system, not her own account, and the AI proofread is not implemented yet. Security comes with the real approval and sign-in later; the site holds no non-public data unless she adds some. Every client page says so in a banner. **The demo is Prototype access mode**, with no setting of its own and no infra change: `SiteMode.IsDemo` is registered from the mode in `AccessPolicies.AddFeatureAccess`, and pages ask it rather than the mode. Prototype is refused in Production, and only the test site runs it. **Export-in-demo exception:** on the demo only, an approved campaign whose rules pass exports without the proofread, through `CampaignGate.DemoReview`; the report keeps `Proofread` false and carries the approval (`DemoApproval`), and every block and the export section say "Not proofread by AI yet". **Enforced (production) keeps the fail-closed gate**, pinned by `EnforcedIsNotTheDemoTests` (no banner; an approved campaign still has no export and no copy control) and `The_rules_alone_never_unlock_export`. Approvals were first kept in the save blob's own metadata; the owner moved them into Table Storage on 2026-10-09 (see "Approvals and the activity trail").

## Built (on `main`)

| Area | Where | State |
|---|---|---|
| Campaign model, typed benefits | `src/Neelam.Campaigns` | Done, tested. Since 2026-10-03 a campaign is a subject, a preheader and **template-defined blocks** (heading, greeting, paragraphs, offer, button, sign-off, fine print), derived from the Beauty Bank email as a sample of one; rules attach to block types. The membership email is now one template among others |
| Template editor model (`TemplateEditor`, `TemplateAdvice`, `BlockGuide`, `TemplatePreview`) | `src/Neelam.Campaigns` | Done, tested (2026-10-03); no page yet. Start blank, open a template, or copy one ("Copy of …"); add, label, move and remove blocks; fix a block's content or leave it to each campaign. Errors are only what stops a save: the `CampaignTemplate` rules, plus fixed content left empty and a link that is not a web address. **Advice comes only from the checks:** the template's fixed content goes through `CampaignReview` as every campaign's will, plus "an offer needs a button" when the template has no button block (a warning when the button is optional). A fixed medical term draws advice only when there is no fine-print block for a disclaimer. The preview is the export's own rendering, with placeholders such as "‹Opening: written for each campaign›". The block guide names only rule ids the checks report, and a test holds it to that |
| Rule checks (`CampaignReview`) | `src/Neelam.Campaigns` | Done, tested |
| Gate + dismissals (`CampaignGate`) | `src/Neelam.Campaigns` | Done, tested with a fake proofreader |
| Templates, drafts, slot origins, tier copy | `src/Neelam.Campaigns` | Done, tested |
| Draft/template JSON (`CampaignJson`) | `src/Neelam.Campaigns` | Done, tested; schema 2 since the block model (nothing was ever saved as 1) |
| Export to editor blocks (`EditorExport`) | `src/Neelam.Campaigns` | Done; block mapping to Square **unverified** |
| Claude proofreader | `src/Neelam.Campaigns.Claude` | Compiles; parsing tested; **never called live**. Its instructions no longer assume "a medical aesthetics clinic" (2026-10-03). Instead the clients table's description of each business (owner's call: the table is metadata to guide the LLM) reaches it through `CampaignGate` as a `BusinessContext`, inside `<business>` tags marked as background, never instructions |
| Blob store (`CampaignStore`, `AzureBlobBackend`) | `src/Neelam.Campaigns.Storage` | Tested in-memory; checked once by hand against Azurite. Reworked 2026-10-03 (step 1 of the rework). Bicep provisions a container per client from a `clients` list in `infra/main.bicepparam` (and `test.bicepparam`), the `settings` container, and the three tables. The app's identity holds data roles, each scoped to a container or table; on the test deployment only, the developer (`developerPrincipalId`) also holds Blob and Table Data Contributor on the account. The app opens stores per client through `ClientStores`; `ClientName` refuses `settings`. Step 4 (2026-10-03): each client's catalog (`catalog/`) and check policy (`policy/`) live in its container, and its look in `settings/{client}/`, all as `DocumentStore<T>`: timestamped versions, newest in force, delete the newest to undo. `ClientStores.PolicyInForceAsync` gives the client's own policy, or the starting defaults until it saves one, for `CampaignReview` / `CampaignGate`. `ClientLook` has only an accent colour so far |
| Metadata tables, caller from sign-in (`TableMetadata`, `CallerClaims`) | `src/Neelam.Campaigns.Storage` | Row mapping and claim mapping tested; On the test deployment the app reads the clients table with its own identity (`/healthz`); **never run against a real sign-in token**. Clients (name, Entra group, display name) and support grants (kept after expiry as the record) behind `IClientDirectory` / `ISupportGrantStore`. An update changes only a client's display name and description (`ClientDirectoryRules`): its name and group are who it is. `CallerClaims` turns the sign-in into a `Caller`: groups map to clients through the clients table, and the Operator app role makes the operator |
| Image library (`ImageLibrary`) | `src/Neelam.Campaigns.Storage` | Done, tested in-memory (2026-10-03). A client's photos for header and image blocks to choose from: one blob each under `images/` in the client's own container, named by the photo's escaped name (a "/" can never make a path), with its name, alt text and date in metadata. JPEG, PNG, GIF and WebP only; 10 MB per photo, a judgment rather than a Square limit. Names are unique ignoring case; deleting a photo is final. The stores now work in bytes (`IBlobBackend`), with JSON as a thin layer over them (`BlobText`). No page uses it yet |
| Shared packages | `Janet.Azure.Storage`, `Janet.Entra` 0.6.0 (from `..\Janet.Shared`) | Since 2026-10-03, every blob and table client comes from one `StorageClients` built at startup: the app's credential, a shared retry budget, and https-only endpoints with no SAS. Since 0.5.0 the create-only upload (fails rather than overwrites) and the binary read are `Janet.Azure.Storage` extensions on the container client, so `AzureBlobBackend` only delegates. 0.6.0 adds typed JSON documents over the same path (`TryCreateJsonAsync`, `ReadJsonAsync`, `BlobContent.ReadJson`); Neelam keeps reading its documents as text because `CampaignJson` owns their mapping and schema check, and takes only the shared JSON content type. Reading the sign-in is `EntraClaims`; Neelam keeps only what groups and roles mean. ImageSelectorV2 uses the same storage package (its commit a4730c6, on its staging slot since 2026-10-03, where its table calls succeed) |
| Access check (`AccessCheck`, `SupportGrant`) | `src/Neelam.Campaigns.Storage/Access.cs` | Done, tested; pure, nothing wired to it yet. Members reach only their own client. The operator manages clients and works on any client's look, but reaches client data only under an active grant from that client; a standing grant (no expiry) covers Neelam until 1.0. Only a member of a client can give its grant. Nobody else reaches anything |
| Feature policies and the caller (`AccessPolicies`, `ICallerSource`) | `src/Neelam.Web/Security` | Done, tested. Policies `Campaigns`, `Campaigns.Review`, `Campaigns.Templates` (added 2026-10-03 for the template editor, so changing layouts can be granted apart from writing campaigns), `Client.Settings`, `Client.Look`, `Operator`; permissive in Prototype, the Entra app role in Enforced, and the tests always run Enforced. Pages get the `Caller` from `ICallerSource` and still run `AccessCheck` themselves. In Prototype it is a fixed caller, the operator and a member of the one client named by `Prototype:Client` (`test-salon-one` in `appsettings.Development.json`; the Bicep writes it from the first listed client only when `environmentName` is `Test`); the app refuses to start in Prototype without it. In Enforced it is the sign-in through `CallerClaims`. `Prototype:Client` goes at rollout |
| Startup credential guard | `src/Neelam.Campaigns.Storage` | Done, tested |
| Web host | `src/Neelam.Web` | Wiring and guard and hand-written CSS with no framework (2026-10-03). **Two layouts since 2026-10-09**: the client-facing header (How it works, Templates) carries no admin links, and admin pages live under `/admin` with their own header (Clients, and a link back to the client view), so a client's people are never shown a mixed admin/user page. **How it works** (`/how-it-works`, anonymous): the workflow (template, write, check, approve, copy into Square) walked through with the Beauty Bank email Neelam sent -- its blocks, the email as sent, the rule findings computed live, and a corrected version as the blocks to paste. The email is library sample content (`BeautyBankEmail`), nothing from storage; the AI proofread is stated as not switched on. Open to anyone with the URL: **protected only by obscurity, accepted by the owner for now** (2026-10-09), since the email already went to Neelam's customers -- revisit when sign-in lands. **Owner's exception, 2026-10-09:** the test site may hold content derived from the Beauty Bank email (the email and the Membership announcement template, e.g. seeded into test-salon-one for walking Priya through the site): it is linked from nowhere, and the campaign is already public, so even leaking all of it reveals nothing. The rule that the test deployment holds no client data still holds for anything not already public. Onboarding a client's own details (business description, look) through a client page is wanted later. **Clients page** (`/admin/clients`, was `/clients`, Operator): lists clients, adds one (name, Entra group ID, display name, business description), edits display name and description; it says adding a row is half of onboarding, since the container comes from the Bicep `clients` list. **Templates** (`/templates`, `/templates/new`, `/templates/{id}`; `Campaigns.Templates`): the client's templates (newest version of each), new, copy and edit. The editor binds to `TemplateEditor` through `TemplateSession` (storage), which saves each version as a new blob and undoes the last save, after a confirmation, by marking it for the sweep (restorable for the grace period; see "Undo is forgiving for a day" above). Header and image blocks name a photo with suggestions from the image library; the library has no upload page yet. Which client a page works in is `ClientWorkspace`: the caller's one client, through the access check (several clients, and the operator under a grant, are not built). Since 2026-10-09 the Campaigns and Templates lists name the client by its display name from the clients table (`ClientWorkspace.DisplayNameAsync`; "Neelam Aesthetics" for test-salon-one on the test site), falling back to its name when the table has no row or no display name. Interactive server render mode on the Clients and editor pages only. Page tests go through the enforcing app: nobody gets 401, the wrong role 403, and the right role 200. **Campaigns** (built 2026-10-09, eb008ea; `/campaigns`, `/campaigns/new/{template}`, `/campaigns/{id}`; `Campaigns`, client layout): the client's campaigns (newest version of each) with New campaign per template, and the editor, which binds to `CampaignEditor` through `DraftSession` (storage). Every field writes through to its draft slot; text the draft cannot hold empties the slot, says why and blocks a save that would lose it; benefits are typed fields per kind; Copy tier goes through `OfferDraft.CopyTier`, so the copy keeps the name and price (owner, 2026-10-09; the rule findings flag both until either tier changes) and each copied benefit is marked unchecked, with Confirm, until confirmed or changed. A save is a new version, unfinished drafts included; undo takes back the newest after a confirmation, restorable for the grace period, as for templates. Another client's campaign, by its id, is not found. **Checks while parts are missing (owner's decision, 2026-10-09):** each missing part is a "Must fix" item under Still to do, in the draft's wording ("Offer › Terms link has not been filled in."); the rule findings run over what is filled in (`DraftSoFar`: blocks with a value, an offer once anything in it is written, its tiers up to the first unfinished one so tier numbers match the form); the preview is the email so far, each missing part a placeholder such as "‹Offer › Terms link: not filled in yet›". Findings that follow from a missing part (e.g. a recurring offer needs a terms link) show too. Missing parts still block: no `ReviewReport` exists until the draft builds, and a complete campaign behaves as before. Still to do now follows the offer and photo fields as they change (they were child components that did not re-render the page); no test drives a Blazor circuit, so that is untested. *(Historical, eb008ea: "Export stays locked", no copy control and no call to `EditorExport.Blocks`. Superseded on the demo by the approval and demo export, row "Demo site, approval, demo export" below: there the page calls `EditorExport.Blocks` on `CampaignGate.DemoReview`'s report and offers copy buttons and the assistant's JSON. Enforced still shows no export, as the proofread is not on.)* Frictions left for the owner (eb008ea): Copy tier is disabled while a benefit is empty; a copied benefit's "Tier N" label goes stale if tiers are removed; an empty benefit is saved without its picked kind; a draft keeps its template's name but not its id; the preheader is not offered. The deploy agent used the campaign editor in a browser on the test site on 2026-10-09, which is how the Still to do lag was found |
| Monitoring | `infra/main.bicep`, `src/Neelam.Web/Program.cs` | Application Insights over a Log Analytics workspace, both with local auth off. The site's identity has Monitoring Metrics Publisher, and Bicep fills `APPLICATIONINSIGHTS_CONNECTION_STRING` from the resource. The app uses the Azure Monitor distro with the same credential as storage, only when that setting is present. Default telemetry, blob dependencies included (decided 2026-10-02). **Live on the test deployment since 2026-10-03:** requests and traces arrive in Application Insights with local auth off, so the managed identity signs the telemetry |
| Infrastructure | `infra/main.bicep` | Builds and lints; settings pinned by tests. **Deployed once, as the test deployment** (2026-10-03, `neelam-test-rg`): shared keys off, the three containers and the three tables of the time, `approvals` among them (the template now also has `activity` and `knownItems`, which need a live infra deploy), every data role held by the site and scoped to one container or table. Production not yet deployed. **Redeployed the same day** (infra, then code at a2ec9b4): the site now has `Prototype__Client = test-salon-one`, and `/healthz`, `/clients`, `/templates` and `/templates/new` answer 200 there with prototype access. Not yet done: onboarding test-salon-one through the Clients page, and building Neelam's membership template through the editor in a browser, both needing a person at the browser. The deploy script's verify step failed on a script error while the push itself succeeded (thread item in the `neelam-aesthetics` area). **Developer access live since 2026-10-08:** the infra was redeployed (deployment `test-20261008-233606`) through Janet's native `azure_deploy` after an `azure_whatif` showing exactly two creates, the developer's Blob and Table Data Contributor on the test account. `storage_probe` then read all three containers and three tables from the desktop as the developer (all empty: nothing onboarded yet); it had been 403 AuthorizationPermissionMismatch on all six before, and stayed so for about two minutes after the deploy while the roles propagated |
| Key-auth policy | `infra/main.bicep` | 13 built-in policies assigned with Deny at resource-group scope, 4 App Service ones with AuditIfNotExists (no built-in Deny exists for basic publishing credentials). GUIDs read from Azure/azure-policy on 2026-10-01. **Never deployed**; deploying needs Owner or Resource Policy Contributor. On 2026-10-02 the same 17 were also assigned by hand at **subscription** scope (names `deny-`/`audit-` plus the first 8 characters of the definition id), so the resource-group copies in this template are now redundant but harmless |
| Demo site, approval, demo export | `SiteMode`, `MainLayout`, `CampaignEdit`, `DraftSession`, `CampaignStore`, `CampaignGate.DemoReview`, `wwwroot/copy.js` | Built 2026-10-09; replaces "Export stays locked" in the Web host row for the demo. **Banner** on every client page (MainLayout, not admin) when `SiteMode.IsDemo`. **Approve** (How it works step 4) on the campaign page: a saved campaign with no missing part and no Must fix, approved by a typed name (no sign-in yet) at the server's UTC time (`TimeProvider`). Kept in the `approvals` table since 2026-10-09 (was `approvedby` / `approvedat` blob metadata; see "Approvals and the activity trail"). `DraftSession.CurrentApproval` is the newest save's approval only while the form still holds exactly that save, her label aside (compared as draft JSON without the label), so any other edit or later save means not approved, and the page says which; undo withdraws the approval (a restored save comes back unapproved); Withdraw withdraws it. Approving takes the `Campaigns.Review` feature, checked in the page (Prototype lets everyone through). **Export** (step 5), demo only: `EditorExport.Blocks` of the demo review, each block with copy buttons (one page-wide listener in `copy.js`, no interop; a button gets its label and its link copied separately) and "Not proofread by AI yet". Enforced shows the old "Export comes once the AI proofread is switched on". **How it works** steps 4 and 5 no longer say "Being built"; they point to Campaigns, and step 5 describes the demo's marked export only on the demo. Untested in a browser: the copy buttons (no test drives JavaScript) and the approve and withdraw clicks (no test drives a Blazor circuit; the page tests set approvals through the store) |
| Site shell (polish, 2026-10-09) | `Program`, `App.razor`, `BuildStamp`, `Home`, `OperatorPagesInDemo` | **Static files** are mapped with `MapStaticAssets` (was `UseStaticFiles`) and `App.razor` links `app.css` and `copy.js` through `@Assets`, so their addresses carry a content hash and a returning browser never pairs an old stylesheet with a new page (which had shown the error bar). The access contract test passes over the development-only fallback `MapStaticAssets` adds when run from build output. **Build stamp:** the deploy has always written "Build N (local) \| Commit ... \| Branch ... \| time" to the App Service setting `LATEST_BUILD_INFO`, which nothing read; every page's footer now shows it (nothing in a local run). `.claude/worktrees/` is git-ignored, so agent worktrees no longer make the stamp read "+dirty". **Home page** on the demo says what she can do there; in Enforced it still says sign-in is not set up. No list builds a possessive from the client's name ("Templates for ...", "Photos for ..."; the Campaigns list's wording came with the campaign editor's polish). Known items show a tier's price as the campaign does, "$499/month". **Operator pages on the demo** answer 404 (see Decided) |

## Template baseline (owner's decision, 2026-10-09; built the same day)

The parts every template should have, as a list of block types held as data (`TemplateBaseline`).

- **Decided by the owner:** the operator provides a standard baseline; each client may save its own, which replaces the standard for that client. Deleting its own ("Use the standard baseline") makes the standard apply again; saving an empty one is the only way a client has no baseline. In UI text it is "the standard baseline", never Neelam's: Neelam is a client, not the operator. **A warning, never a block:** a template lacking a part still saves, and the editor lists each missing part ("Your templates usually have a sign-off; this one doesn't."), since a short note such as "we're closed Monday" may rightly have no button. A part counts as present when the template has a block of that type, required or optional (her Membership template's photo is optional, and counts).
- **Evidence for the standard one:** Neelam's five sent emails (Aug-Sep 2026). Every one had a header, a headline, a call-to-action button and a photo; greeting and body text were in four of the five; the latest signs off from the team rather than from one person. So the standard is Header, Heading, Sign-off, Button and Image; greeting and body are not in it. Square's own footer carries the legal address and unsubscribe, so templates never include them.
- **Where it lives:** a client's own baseline at `{client}/baseline/{stamp}.json` in its own container; the operator's standard at `settings/_standard-baseline/{stamp}.json` (a leading underscore, which no client name can have). Both are `DocumentStore<TemplateBaseline>` through `ClientStores`, mirroring the check policy: `ClientStores.BaselineInForceAsync` gives the client's own, else the operator's saved standard, else `TemplateBaseline.Standard` in code (as `CampaignPolicy.Default` is for policy). Nothing is seeded into storage. Pages reach it through `ClientWorkspace.ClientDataAsync`, as for templates.
- **UI:** the Templates page has a short "Every template should have" section (an interactive component, `BaselineSection`): the parts, whether it is the standard or her own, and under "Change it" a checklist with "Save as your own", "Save it empty: no warnings", and "Use the standard baseline" when she has her own. The template editor lists missing parts in its Save card. The checklist names block types as the editor does (Heading, Button, Image), not "Headline", "Call to action", "Photo".
- **Not built:** no admin page to edit the standard baseline (thread item in the `neelam-aesthetics` area). Until one exists, the standard is changed by saving a document to `settings/_standard-baseline/`. No test drives the section's buttons in a Blazor circuit; the store calls behind them are tested.

## Registered phone numbers (owner, 2026-10-09)

Neelam's latest email signs off "Snohomish, WA | 425-877-8646"; Square's record of the business has (425) 773-5261 (likely a move from a personal number to a company one). Decided: the numbers a client may publish are **registration data**, and a rule flags any other.

- **Where they live:** `ClientRecord.Phones` (`PhoneNumbers`, any number of them, e.g. a main and a booking line), a `Phones` property on the clients-table row as comma-separated digits with the country code (`14258778646`), left out when none; old rows read as none. The Clients page (`/admin/clients`) adds and edits them, one per line, written any usual way, and shows them formatted, "(425) 877-8646". `PhoneNumber` assumes +1 for ten digits.
- **The rule:** `phone-registered`, a warning ("Worth a look"): "425-877-8646 isn't one of your registered numbers ((425) 773-5261)", at the block it is in. It reads every text fragment the other rules read (fixed template text included) plus button and terms links, for `tel:`. North American forms only: 425-877-8646, (425) 773-5261, 425.877.8646, +1 425 877 8646, tel:+14257735261. **No numbers registered, no finding.**
- **How the rule gets them:** `ClientRecord.Business` (`BusinessContext`, which already carried the name and description to the proofread) now carries `Phones`; `CampaignReview.Check`, `CampaignEditor.Status` (both the complete and the `DraftSoFar` paths) and `CampaignGate.ReviewAsync` take it. The campaign page reads it with `ClientWorkspace.BusinessAsync`.
- **Not done:** no number is registered anywhere: the owner confirms which are current. The template editor's advice (`TemplateAdvice.For`) is not given the registration, so a stale number in a template shows only once a campaign uses it. The proofread prompt is not told the numbers.

## Images hosted on Square (owner, 2026-10-09)

**Decided:** an image-library entry can reference an image hosted on Square instead of an
uploaded copy. Square is where the client's images live (she pastes the email into Square and
picks images from Square's own library), so referencing Square's copy keeps our preview from
drifting from hers, and no image file is copied or stored here.

- **Entry:** `LibraryImage.SquareUrl` (null for an upload). `ImageLibrary.AddFromSquareAsync(name, address)`
  stores an empty blob at `images/{escaped name}` in the client's own container, its name and
  address in metadata as an upload's name and alt text are. Old entries read unchanged, as uploads.
- **Hosts, as data:** `ImageLibrary.SquareHosts` = `postoffice-production-f.squarecdn.com`,
  `square-web-production-f.squarecdn.com`, `square-postoffice-production.s3.amazonaws.com`;
  https only, with no userinfo and no port other than the default; any other host refused with a
  message naming it (tests cover userinfo, another port, a host in capitals and a tampered stored
  entry). Checked again on read: a stored entry that fails is listed, marked
  `LibraryImage.NotASquareAddress` and with no address, so `/images` says "Not a Square address --
  remove it" with Delete, it is never shown as an image, and its name stays taken (2026-10-09; it
  used to be skipped, so it could be neither seen nor deleted). The
  query string is kept whole: Square crops and sizes through it (`?enable=upscale&height=196&width=640`, `crop=1:1`).
- **Previews:** the template page, the campaign page (preview and fixed parts) look photos up
  by name in the client's library (`ImageLibrary.Find`) and show a Square photo from its
  address (`PreviewPhoto`). A name the library does not hold shows the same plain message on
  both pages and in the photo field (`ImageLibrary.NotInLibrary`), which fixes the report that
  the template page said "no photo by this name" while the campaign preview showed "Photo: …".
  How it works passes no library and shows names, as before. An upload is still shown by name:
  nothing serves upload bytes yet.
- **Page:** `/images` (Image library, policy `Campaigns`, client layout, "Images" in the
  header) lists the client's photos and has **Add from Square** (name and address). Each photo
  shows its name and image, its Square address behind a "Square address" disclosure, and Delete
  beside the name; Delete's confirmation opens in place above the image. Deleting a
  Square entry removes only the reference here, never anything at Square, and the page says so.
  There is still no upload page. This supersedes "No page uses it yet" in the Built table.
- **Not done:** no entries added on the live or test site; that waits for the owner's approval
  after deploy. The Beauty Bank sample names "Principals toasting" (header) and "Principals
  seated" (body photo) with no Square addresses, so adding them needs the addresses from her
  Square library.

## Campaign label (owner, 2026-10-09)

Her two sends of the Beauty Bank email share the subject "WE'RE TURNING ONE!", so her list could
tell them apart only by time. **Decided:** a campaign has an optional label, for her own use only.

- **Where it lives:** `CampaignDraft.Label`, saved by `CampaignJson` as an optional `label` in the
  draft's JSON (trimmed; left out when blank, so schema stays 2 and older drafts read as
  unlabelled). Never in a blob's name or metadata; the metadata `title` (the subject) is unchanged,
  pending the owner's open call on whether the subject leaves metadata.
- **Never in the email:** `Build()` and `DraftSoFar` leave it out, so no rule checks it and
  `EditorExport` never shows it.
- **Editor:** "Label (just for you)" at the top of the campaign page (`CampaignEditor.Label`).
- **List:** `/campaigns` shows the label as the heading with the subject under it.
  `DraftSession.ListLabelledAsync` reads each campaign's newest save in use, once, all at the same
  time: **one blob read per campaign** on every list (fine for tens), from the client's own
  container only. Undone saves are never listed, so their labels are never read.
- **Approval:** since the approvals moved to Table Storage (2026-10-09), a label-only change keeps
  the approval: it is compared on the draft without its label, and a label-only save carries the
  approval to the new save. See "Approvals and the activity trail".
- **Not done:** no live campaign is labelled; that is a separate step after deploy.

## Approvals and the activity trail (owner, 2026-10-09; built the same day)

**Decided by the owner:** approvals live in Table Storage, and every action is recorded as an
activity event, deletions included. The record never holds the content: "We want as much tracking
as we can - deletions too are recorded, but not the data." This refines "a deleted save leaves no
record" (Decided, above): that rule is about blob history, so a deleted save's *contents* stay
unrecoverable, while an event saying which save was deleted, and when, is wanted.

- **Approvals** (`approvals` table, already deployed with the 2026-10-03 tables): partition = client,
  row = `{campaign id}_{save stamp}`; `ApprovedBy`, `ApprovedAt` (UTC), `Withdrawn`, `WithdrawnAt`.
  `IApprovalStore` (`TableMetadata`); `CampaignStore.HistoryAsync` reads them for one campaign from the
  client's own partition. Nothing about an approval is in the blob any more (`approvedby`/`approvedat`
  are gone, and old ones are ignored). Withdraw and undo mark the row withdrawn, with the time; undo,
  the sweep and any delete leave the row as history, and it simply matches no save in use.
  **Label-only saves keep the approval** (coordinator's request, 2026-10-09): what an approval is of
  is the draft without her label, so changing only the label, saved or not, keeps it; the save carries
  a new row with the same approver and time (`CampaignStore.KeepApprovalAsync`), and the trail records
  `ApprovalCarriedToLabelOnlySave` on the new save, by whoever saved it, ids only. Any other change voids it.
- **Activity** (`activity` table, **new: needs a live infra deploy**): one row per event, partition =
  client, row = `{UTC stamp}-{guid}`; `Entity` (Campaign, Template, Baseline, ImageEntry,
  ClientRegistration, KnownItem), `EntityId`, `SaveStamp` (left out when none), `Action`, `Actor`, `At`
  (`TimeProvider`). Actions: Saved, Approved, ApprovalWithdrawn, ApprovalCarriedToLabelOnlySave,
  Undone, Restored, DeletedBySweep, BaselineSaved, BaselineResetToStandard, ImageEntryAdded,
  ImageEntryRemoved, ClientRegistered, ClientChanged, KnownItemAdded, KnownItemChanged, KnownItemRemoved. **No content, ever**: ids only; an image entry is named by a random id kept in its
  metadata (`entry`), never by its name. A test runs every action with real campaign text, a label, a
  registration and a photo, and fails if any of it appears in any row.
- **Actor:** the sign-in's name (`name`, else `preferred_username`, else the object ID) in Enforced;
  in Prototype "demo user", except that an approval goes against the name typed for it. The sweep is
  "undo sweep".
- **Never blocks:** `ActivityRecorder` catches and logs a failed write (ids only) and the action goes
  on. Until the `activity` table is deployed, every write fails that way and is logged. A
  cancellation the caller asked for (its token cancelled) is not a failed write and goes back to the
  caller; a cancellation nobody asked for, such as a timeout inside the table client, is logged as one.
- **Not built:** no page reads the trail (thread item "activity view (client and operator, under
  support-grant rules)"). Dismissals are not in the table yet.

## JSON export for an assistant (owner, 2026-10-09)

**Decided:** an approved campaign can be downloaded as JSON for the client's own assistant agent,
which does the scut work of filling in Square Marketing's email editor and then checks that what is
in Square matches the approved campaign, with no difference between the two.

**Also decided (owner, 2026-10-09): the app has no access to the client's Square account, and none
is to be added for now, especially not before the site is locked to her.** No Square API calls,
OAuth, tokens or credentials, and nothing in the app that signs in to Square or pushes to it. The
JSON is a file she downloads and hands to her own assistant, nothing more. The only Square
addresses the app holds are the image URLs she already keeps in her library.

- **Same gate:** `AssistantExport.Build` calls `EditorExport.Blocks` on the same `ReviewReport` as
  the copy blocks, so there is no second path out. Demo: approved, no Must fix, `proofread: false`
  and the notice "Not proofread by AI yet". Enforced: the proofread report and an approval, both;
  the page still offers no export in Enforced, since the proofread is not on.
- **Shape:** `docs/assistant-export.schema.json`, schema version 1 (there was no `docs/` or
  `contracts/` before; `docs/` was chosen). Instructions first, then campaign (id, label, subject,
  preheader, template), blocks, review (proofread, approval, "Worth a look" findings as text,
  notice). Each block has a stable id (`b1`...), a `type` and `formatting` from
  `SquareWidgets.ByKind`, its content, and `expected`: text, link, the photo's library name, Square
  image URL and alt text as Square should show them (`AssistantExport.Normalise`: line breaks as `\n`, runs of spaces
  collapsed, at most one blank line). `contentHash` is SHA-256 over the subject and preheader, then
  the expected content in order, by the recipe in the schema; a test recomputes it from the file
  alone. The photo's name is in it (2026-10-09, from review), so swapping a photo with no Square
  address still moves the hash; the subject and preheader too (2026-10-09, from review), since the
  assistant checks Square's against them. Changed in place under version 1: nothing consumes it yet.
- **Square widget mapping, as data:** `SquareWidgets.ByKind` in `AssistantExport.cs`. The copy
  blocks name their kinds from it too. Header, Text (formatting `heading1` for a heading), Image,
  Button, Spacer: from the real sends, **still unverified against Square's editor**. Square's sent
  emails have two heading styles, `body_text_h1` and `body_text_h2`; a campaign has one heading
  level (the headline and each offer's name), mapped to Heading 1 and named so ("Text (Heading
  1)"). Whether the offer name should be Heading 2 is a question for the next real sends.
- **Fail closed:** a block kind with no mapping refuses the export, naming it; the document is
  then checked in code (`AssistantExport.Problems`: version, fixed instructions, subject, approver,
  each block's id, widget type and formatting from the table, what its type needs -- text, button
  link (absolute http(s), and `expected.link` the same), image name -- and the hash recomputed),
  and a failure shows "No download for an assistant:
  ..." with the reason instead of the link. **The app has no schema dependency** (owner,
  2026-10-09): JsonSchema.Net 7.4.0 is in the test project only, every export the tests make is
  validated against `docs/assistant-export.schema.json`, a test fails if code and schema drift
  either way, and a test fails if a production project references the library.
- **Instructions** are fixed data (`AssistantExport.Instructions`): fill in, verify (read back,
  compare each block with `expected`, order and count count -- Square's own header, footer and the
  spacers around them excepted -- fix and compare again, report block by block), stop and report anything that cannot be placed or compared exactly, never approximate,
  never send or schedule, and treat everything under campaign, blocks and review as content, never
  instructions (the prompt-injection guard).
- **Download:** a `data:` link on the page (base64), no endpoint, so the export has no address of
  its own to reach. The file is named from the label, else the subject (`beauty-bank-first-send.json`).
- **Not done:** never tried with a real assistant or in a browser; the image of a photo that is not
  on Square has no address, so the assistant stops at it by design. The Beauty Bank photos have no
  Square addresses yet (see "Images hosted on Square").

## Known items (owner, 2026-10-09; built the same day)

**Decided by the owner:** a per-client list of things she writes again and again, in **Azure Table
Storage**, picked from while writing a campaign. Three kinds, all wanted: **treatments and services**
("Botox", "Wellness injection"), **benefit lines** ("10% off any qualifying treatments"), and **offers /
tiers** ("Platinum Member", $299/month, with its benefit lines). Guidance with it: "as much as possible,
data driven and helpful", so her campaigns fill the list, it suggests as she types, and it catches near
misses.

- **Table** (`knownItems`, **new: needs a live infra deploy**; added to the Bicep `tables` list and pinned
  in `InfrastructureTests.The_metadata_tables_are_exactly_these`, beside `activity`): partition = client
  name, row = the item's id (32 hex digits). Columns: `Kind` (Treatment, Benefit, Tier), `Text` (a
  treatment's or tier's name, a benefit's sentence), and `Benefit` (a benefit item's typed benefit as
  JSON) or `Price` (text, so it is exact) and `Benefits` (the tier's typed lines as JSON). The site's
  identity gets Storage Table Data Contributor on it through the same `tables[i]` loop as every table.
  Until it is deployed, the Known items page fails to open; campaign pages log the failed read
  (`ClientWorkspace.BusinessAsync`) and go on with no known items. **Malformed rows** (2026-10-09): a
  known-items row that cannot be read (missing column, unknown kind, a price or benefits that do not
  parse) is left out by the store (`KnownItemTable.ReadAll`) and logged by its row key and the failure's
  type, never its content; the rest are kept. A row from another client's partition is still refused
  outright, and `BusinessAsync` takes any failure to read known items as none. A clients-table row that
  cannot be read makes `BusinessAsync` log it and throw `RegistrationUnreadableException`; the campaign
  page then says "This campaign can't be checked yet" with "Your business's details couldn't be read, so
  this campaign can't be checked yet. Nothing has been changed, and the problem has been logged for
  fixing." rather than checking it as if no phone numbers were registered. Untested in a circuit, as the
  rest of the page; the workspace behaviour is tested. Other pages that read the clients table
  (sign-in, Campaigns list, Clients page) still fail on such a row.
- **Benefits stay typed.** The campaign model has no free-text benefit (README "Why": "50% Complimentary"
  cannot be expressed), so a known benefit is a typed `Benefit`, its text the sentence it reads as.
  Picking one puts the same kind and values in the form.
- **A tier carries its lines' text, not references to benefit items.** A tier goes out as a whole, so
  changing or removing a benefit item must not quietly change a tier; a tier saved from a campaign has
  lines that were never benefit items; and one row is the whole tier, with no reference to dangle.
- **Scoping:** pages reach it only for the client `ClientWorkspace.ClientDataAsync` gave
  (`ClientWorkspace.Known`, `IKnownItemStore`, `KnownItemTable`). Every query names one partition, and
  a row from any other is refused when read. `KnownItemStoreRules` (both stores): an item says the same
  thing once per kind (ignoring case), a tier needs a name and a price above zero, an item keeps its
  kind when changed.
- **Her page:** `/known-items` ("Known items" in the client header, policy `Campaigns`, client layout,
  no admin links). A section per kind: add, change (in place) and remove. A tier is built from a name, a
  price and lines, each picked from her benefit lines ("Pick a benefit") or typed. The page binds to
  `KnownItemsSession`, which the tests drive.
- **In the campaign editor** (`OfferFields`, with the shared `BenefitFields` and `KnownItemLists`):
  "Pick a benefit" under each tier adds a known benefit as a new line; "Add a known tier" under the tiers
  adds a whole tier; the tier name and each benefit's **Item** field (the treatment, for "something free"
  and "percent off an item", the only place the block model names one) suggest her known tier names and
  treatments as she types, through the browser's own list (`datalist`), with free typing still allowed.
  Picked text is ordinary text she entered (`Origin.Entered`), never "not yet checked". A picked tier
  that repeats another's name or price is let in and flagged by `tier-names-unique` /
  `tier-prices-increase`, as a copy is. **Save as a known item** next to each tier and each benefit line
  saves it exactly as written (or says it is already known); a benefit with an Item also offers
  **Save "X" as a known treatment** (2026-10-09, with the rule below).
- **Checks against known items: offer details only (owner's decision, 2026-10-09).** The first version
  flagged prose-like misfires ("Social" as near "Facial", "Lasers" as near "Lashes", "Facials" as near
  "Facial"). The owner's ruling: assume good faith for existing words and be strict only on offer
  details; a split by field "feels more professional than a one size fits all approach". So
  (`known-item`, all "Worth a look", never a block; `CampaignReview.KnownItemChecks`):
  - **Prose** (headline, greeting, opening, closing, fine print, any free text): never compared. Spelling
    in prose is the AI proofread's job; there is no dictionary.
  - **Offer details**: each tier's name against her known tiers, each benefit's Item against her known
    treatments, each benefit's sentence against her benefit lines and the lines of her known tiers.
    Close but not the same: "Did you mean 'Wellness injection'? It's in your known items." Not known at
    all: "'Hydrafacial' isn't one of your known treatments." (or "tiers", "benefit lines"), with the
    one-click save beside the field. A known tier's name at another price: "'Platinum Member' is
    $299/month in your known items; here it is $249/month." When a benefit's Item draws a note, its
    sentence is not noted as well.
  - **Threshold** (`KnownItemMatch`): the same words ignoring case, spacing and a plural ending on any
    word ("Facials" = "Facial", "Lashes" = "Lash") is known and says nothing; otherwise a near miss is at
    most 1 edit for a known item under `KnownItemMatch.LongItemLength` (10) characters, 2 at or above,
    with the same digits ("15% off" is not a typo of "10% off").
  - With no known items of a kind, nothing is said about that kind. Only offer details reach the
    assistant export's `worthALook` from this rule (pinned by a test).
  The items reach the rule as the phone numbers do: `BusinessContext.Known`, filled by
  `ClientWorkspace.BusinessAsync`, so it runs on whole and partial drafts and in the gate. Never given to
  the proofread.
- **A store failure never ends her session** (2026-10-09): saving from the campaign editor, and adding,
  changing or removing on the Known items page, catch anything the table client throws other than the
  store's own refusals, log it by the item's id (never its text) and say "Couldn't save that to your
  known items. Your campaign is untouched. Try again in a moment." (on the Known items page, "What you
  typed is still here." / "Couldn't remove that ..."). A change that went through stands when the list
  cannot be read again after it. No `ErrorBoundary` was added: one around the editor would catch only
  child components' failures, not the page's own handlers. Opening the Known items page while the table
  is unreachable still fails, as before; nothing typed is lost there.
- **Activity:** added, changed and removed are `KnownItem` events (`KnownItemAdded`, `KnownItemChanged`,
  `KnownItemRemoved`) by the item's id, never its text.
- **Not done:** nothing is seeded, on the live or the test site. Untested in a browser: the pickers and
  buttons (no test drives a Blazor circuit or a `datalist`). Treatment names written in free text
  (paragraphs) are deliberately not checked against the list (owner, 2026-10-09, above). Picking
  happens in the campaign editor only, not the template editor.

## Open decisions (the owner's to make)

Do not settle these on the owner's behalf. Bring options with a recommendation.

1. **Square editor details.** Partly answered on 2026-10-03 by the two real sends (`.eml` files the owner keeps outside the repo, and phone screenshots):
   - Neelam does not use bullet lists: each benefit is its own line starting with 🤍, and the export now does that with each template's own marker.
   - Neelam's greeting is a fixed "Hi Beautiful🤍"; no first-name placeholder was used. Whether Square offers one is still unknown.
   - Square's blocks seen: header (business name over a photo), spacer, text in heading or paragraph style (one text block holds many paragraphs), image, button. Square adds a reply banner at the top and the footer (address, unsubscribe) itself.
   - The client's image library now exists (see Built); blocks do not pick from it yet, which comes with the template editor. Neelam's colours from the email (button bronze #997c61, header green #225E3E) are look data, to enter through the look settings, not code.
2. **Sign-in.** The app must not show or change campaigns until Entra ID sign-in exists, or anyone with the URL could read and delete saves.
   - Staying secret-free means a federated credential on the app's identity instead of a client secret.
   - Open: App Service built-in auth or Microsoft.Identity.Web? Either can put the group and app-role claims in the token.
   - Open: how clients' people get accounts with no setup of their own. Guests invited into the operator's directory, or Microsoft Entra External ID (an emailed code or an existing account)? External ID is likely kinder for non-technical users, but it is a separate setup.
   - **Owner's constraint, 2026-10-09:** a client's people (Priya first) must not be made to sign in to an unfinished site and then to a new one later. Until the real site exists they see only public, no-data pages (`/how-it-works`); their first sign-in is one that lasts. That favours an identity that is theirs (their own email or existing account), the same for every deployment, and a site address that does not change at go-live.
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
8. **The catalog, now that known items exist** (2026-10-09). `ClientCatalog` (`{client}/catalog/{stamp}.json`: procedures and medications, each with a kind and an optional usual price) has a model, JSON and a store (`ClientStores.Catalog`), but **nothing reads or writes it** outside tests: no page, not the checks, not the proofread. Known treatments hold the same names. Options: (a) known items supersede it, and the catalog code and its README/WIP mentions are removed; (b) keep both, the catalog for prices; (c) give known treatments an optional usual price and kind, then (a). **Recommendation: (a)**, adding a price to a treatment only when a check or page needs one: the catalog is unused, and two lists of the same names would drift. Nothing is deleted or migrated until the owner says.
9. **Customer data in blob metadata** (raised 2026-10-09). The rule says "customer data does not live in blob names or metadata", yet some metadata is her words: each save's `title` is its subject line; an image entry's metadata holds its name, alt text and Square address; saves approved before approvals moved to the table (2026-10-09) may still carry `approvedby` (a person's name), now ignored but not removed. Telemetry that names a blob does not see metadata, but anything that lists blobs does. Options: (a) accept these as the stated exceptions and write them into the rule; (b) move them out: the subject read from the save's JSON (as the label already is, one read per campaign per list), image entries as rows in a table, and the old `approvedby` values cleared; (c) (b) for the subject and the approver, (a) for image entries, whose names are what blocks refer to. **Recommendation: (c)**: the subject and an approver's name are the client's content and a person's name, while a photo's name is closer to an id she chose. Nothing changes until the owner says.
10. **The same finding on both tiers** (raised 2026-10-09). Some checks report per field, so a line both tiers carry draws the same finding once for each tier (for example a known-item note on a copied tier), while others report once naming both (`tier-names-unique`). Options: (a) per tier, as now: each place to fix is listed; (b) one finding naming every tier it applies to; (c) (b) on the page, (a) in the assistant export, which places findings by block. **Recommendation: (b)**, matching `tier-names-unique`: one fix is usually one decision, and two identical lines read as noise.
11. **Tier names that differ only by "Option N"** (found 2026-10-09, checking the first-send sample against the sent email). The real first send named its options "Option 1 Platinum Member" and "Option 2 Platinum Member", so `tier-names-unique`, which compares whole names, does not fire on it; it is still stopped for the same option twice and the same price twice. If the second send was numbered the same way (its `.eml` was not checked), the name rule would not have caught the second send's mistake. Options: (a) leave the rule on whole names; (b) compare names with a leading "Option N" (or "Tier N", "N.") left out; (c) also warn when two names differ only by such a prefix. **Recommendation: (b)**, after checking the second send's text. A rule change, so not made here; the sample now says what was sent, and `First_send_is_blocked_for_identical_offers` pins what the rules say today.

## Suggested next steps, once the decisions above land

1. Rework `7141d54` to the clients-and-access design, before sign-in exists:
   - **Bicep:** a `clients` list, with a container and the app's blob role per client; the `settings` container; Table Storage with *Storage Table Data Contributor* for the app; no `clientName`, no `developerPrincipalId`.
   - **Code:** the access check as a pure, tested component (member, operator, support grant, the Neelam-until-1.0 grant); stores opened per client; the catalog and check policy (client container) and the look (`settings` container), with their stores, the policy feeding `CampaignReview`; `ClientName` refusing reserved names such as `settings`.
2. Sign-in, then the first real pages: list saves, open a draft, save, delete.
3. A draft editor in Blazor, built on `CampaignDraft` / `Slot`. Show each slot's origin and a "confirm" action for copied values. Built 2026-10-09 (see Web host): copied benefits carry Confirm; other slots' origins are not shown.
4. A review screen: findings, dismissals with name and reason, and the export blocks with copy buttons.
5. Persist dismissals and approvals in Table Storage (decided 2026-10-03). Approvals done 2026-10-09; dismissals still open.
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
