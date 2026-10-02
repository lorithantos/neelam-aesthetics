# Work in progress — handover

Status of the campaign-safety work as of 2026-10-02, for whoever picks it up next. The README
describes the design; this file covers where things stand, what was decided and by whom, and
what is still open. Delete it once the PR is merged and the open items have homes elsewhere.

- **Branch:** `claude/email-campaign-safety-system-lr7q1m`
- **PR:** #1 (open, not merged; `main` holds only the initial commit)
- **Tests:** `dotnet test` → 70 passing, no build warnings
- **Bicep:** `bicep build` and `bicep lint` clean (CLI 0.47.16)

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
- Storage is **Azure Blob Storage only**, with no SQL or other database.
- Infrastructure is written in **Bicep**.
- **Managed identity only.** No connection strings, account keys or SAS, anywhere. Restated on 2026-10-01 as "no connection strings at all": `CredentialGuard` now refuses a connection string even when it carries no secret, and the resource group denies key authentication through Azure Policy (see `infra/main.bicep`). Only the server matters; local runs need not prove it.
- **Saves are date/time-stamped blobs.** Any blob can be deleted with no remaining record, so there is no index, versioning, soft delete or logging that would keep one.
- Campaigns are created **from templates**.
- **Tier copy must not merely copy everything.** It leaves the name and price blank and marks copied benefits unreviewed.
- An **AI proofread** is part of the gate.

## Built (all on the PR branch)

| Area | Where | State |
|---|---|---|
| Campaign model, typed benefits | `src/Neelam.Campaigns` | Done, tested |
| Rule checks (`CampaignReview`) | `src/Neelam.Campaigns` | Done, tested |
| Gate + dismissals (`CampaignGate`) | `src/Neelam.Campaigns` | Done, tested with a fake proofreader |
| Templates, drafts, slot origins, tier copy | `src/Neelam.Campaigns` | Done, tested |
| Draft/template JSON (`CampaignJson`) | `src/Neelam.Campaigns` | Done, tested |
| Export to editor blocks (`EditorExport`) | `src/Neelam.Campaigns` | Done; block mapping to Square **unverified** |
| Claude proofreader | `src/Neelam.Campaigns.Claude` | Compiles; parsing tested; **never called live** |
| Blob store (`CampaignStore`, `AzureBlobBackend`) | `src/Neelam.Campaigns.Storage` | Tested in-memory; checked once by hand against Azurite |
| Startup credential guard | `src/Neelam.Campaigns.Storage` | Done, tested |
| Web host | `src/Neelam.Web` | Skeleton only: wiring + guard, **no campaign pages** |
| Infrastructure | `infra/main.bicep` | Builds and lints; settings pinned by tests; **never deployed** |
| Key-auth policy | `infra/main.bicep` | 13 built-in policies assigned with Deny at resource-group scope, 4 App Service ones with AuditIfNotExists (no built-in Deny exists for basic publishing credentials). GUIDs read from Azure/azure-policy on 2026-10-01. **Never deployed**; deploying needs Owner or Resource Policy Contributor |

## Open decisions (the owner's to make)

Do not settle these on the owner's behalf. Bring options with a recommendation.

1. **Square editor details.** The owner does not know yet:
   - whether Square offers a first-name placeholder, and its exact form
   - whether bullet lists survive pasting into a text block. If they don't, export each benefit as its own line with 🤍 in front.
2. **Sign-in.** The app must not show or change campaigns until Entra ID sign-in exists, or anyone with the URL could read and delete saves.
   - Staying secret-free means a federated credential on the app's identity instead of a client secret.
   - Open: who can create the app registration? Use App Service built-in auth or Microsoft.Identity.Web?
3. **Approval.** Is one approver enough, or must a second person approve before export? Must warnings be acknowledged individually, or do only blockers stop export (the current behaviour)?
4. **Claude.** No answer yet. Options: call the Anthropic API directly, or go through Microsoft Foundry in the Azure subscription (`AnthropicFoundryClient`, same proofreader code).
   - An API key, or another way to authenticate, is needed before the proofreader can be run on the two real sends.
   - Server-side refusal fallbacks were left out because they aren't available on Foundry. A refusal currently blocks the email until a person dismisses it.
5. **Monitoring.** Application Insights is configured with a connection string even when ingestion is Entra-only, and "no connection strings at all" rules that out: `CredentialGuard` refuses `APPLICATIONINSIGHTS_CONNECTION_STRING`. Policy already requires any Application Insights or Log Analytics resource to block non-Entra ingestion. Open: leave monitoring out, or find a route that needs no connection string.
6. **Placeholders the owner should replace:**
   - the "Gold Member" tier name and the `example.com` join and terms links in the fixtures
   - the restricted terms, medical terms and emoji limit in `CampaignPolicy`
   - whether "Beauty Bank" and "savings account" are acceptable. They are warnings, not blockers, on purpose: it is a business and legal call.
7. **"Start from last campaign".** Offered but not answered. It would reuse slot origins, so every copied field has to be edited or confirmed.
8. **Copied benefit amounts.** Offered but not answered: should a copied tier's amounts start blank, so every number has to be re-typed? Currently each copied benefit is kept but marked unreviewed.

## Suggested next steps, once the decisions above land

1. Sign-in, then the first real pages: list saves, open a draft, save, delete.
2. A draft editor in Blazor, built on `CampaignDraft` / `Slot`. Show each slot's origin and a "confirm" action for copied values.
3. A review screen: findings, dismissals with name and reason, and the export blocks with copy buttons.
4. Persist dismissals and approvals. By the owner's rule these are also timestamped blobs with no index, and they need a design decision on where they live.
5. First real deployment of `infra/main.bicep`, with `developerPrincipalId` set for local work.
6. A live proofread of `FirstSend()` and `SecondSend()` once Claude access is decided.
7. Consider adding a `CLAUDE.md` to this repo. There is none yet.

## Environment notes

- **.NET:**
  - Targets `net10.0` (moved from `net8.0` on 2026-10-02); App Service runs `DOTNETCORE|10.0`. Sessions are local with SDK 10.
  - The solution is still a classic `Neelam.sln`, a leftover of the SDK 8 days; `.slnx` would now build.
  - Tests use xunit 2.4.2. Use `Assert.Equal` on sorted arrays, not `HashSet`s, for order-free comparisons.
- **Bicep:** Bicep CLI 0.47.16 and Azure CLI 2.90.0 installed with winget on 2026-10-02; `az` uses the PATH `bicep`. Signed in with `az login` on 2026-10-02 (the tenants need MFA, so sign in with `--tenant`).
- **Azurite check (manual):**
  1. `npm i azurite@3`, then make a self-signed certificate for `127.0.0.1`.
  2. Run `npx azurite-blob --oauth basic --cert cert.pem --key key.pem`.
  3. Point a probe at `https://127.0.0.1:10000/devstoreaccount1`, with `SSL_CERT_FILE=cert.pem` and `NO_PROXY=127.0.0.1`.
  4. "Basic" OAuth mode accepts an unsigned JWT with `aud=https://storage.azure.com`, an `sts.windows.net` issuer and a valid expiry.
  5. Stop it with `pgrep -f "node.*azurite"` + `kill`, not `pkill -f azurite`: that pattern also matches the shell running it.
- No test touches the network. Keep it that way, and keep real-service checks as manual probes.
