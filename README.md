# Neelam Aesthetics — campaign safety

Email campaigns go out through Square Marketing, which has no API for campaigns, so mistakes can't be caught by
automation on the sending side. This project moves the email upstream: it is written as
**structured data**, checked by a **two-part gate**, and only then **exported as blocks** that a
person pastes into the editor in order.

```
Campaign ──► CampaignReview (rules: instant, not dismissable)  ─┐
         └─► IProofreader   (Claude: reads it like an editor)  ─┴─► CampaignGate ──► ReviewReport
                                                                                        │
                                                    blockers, or not proofread ◄── no ──┤ CanExport?
                                                                                       yes
                                                                                        ▼
                                                                     EditorExport.Blocks / PlainText
```

- **Rules** catch what can be stated exactly: duplicate tier names, identical tiers, missing
  links, missing terms. A rule blocker cannot be dismissed; it has to be fixed.
- **The AI proofread** catches what a careful reader would: spelling, contradictions, offers
  that don't match their names, placeholder text left in. Its errors block too, but a person
  can dismiss one with a recorded reason and name (`Dismissal`), since a model can be wrong.
- **It fails closed.** If the proofread can't run (outage, refusal, cut off), that is itself a
  blocker. Sending without it takes a named dismissal.
- An AI finding whose quoted excerpt isn't actually in the email is downgraded to a warning
  rather than trusted.
- **A person approves** a saved campaign with nothing left to fix: no missing part and no "Must
  fix" finding. The approval (a name and the UTC time) is a row in the approvals table, keyed by
  client, campaign and that save's stamp, so it belongs to that exact version; the row stays as
  the record when the save is undone or deleted. Any edit or later save is not approved, except a
  change to the client's own label, which is never in the email; undoing a save withdraws its
  approval; an approval can be withdrawn. Approving takes the `Campaigns.Review` feature. Until
  sign-in exists the approver types their name.
- **Every action is on the activity trail**: saves, approvals and withdrawals, undo, restore, the
  sweep's deletions, baselines, image entries and client registrations, one row each in the
  client's partition of the activity table, with who and when. Never the content: things are named
  by id only, so a deleted save's contents stay unrecoverable while the fact of its deletion is kept.

**The demo exception (owner, 2026-10-09).** The test site is a full demo for a client to walk
every step on, and it runs Prototype access, which is refused in Production. There, and only
there, an **approved** campaign whose rules pass exports without the AI proofread, which is not
switched on yet: `CampaignGate.DemoReview` returns a report with `Proofread` false and the
approval in `DemoApproval`, and every exported block says "Not proofread by AI yet". Nothing fakes
a proofread result. Enforced access, which production runs, never takes this path, so there
export still needs `CampaignGate.ReviewAsync` and the proofread; tests pin both sides
(`EnforcedIsNotTheDemoTests`, `The_rules_alone_never_unlock_export`). Real security arrives with
the real approval and sign-in.

## Templates and drafts

**A template defines a campaign's structure, as data.** A `CampaignTemplate` is an ordered
list of blocks, each with a label, a type, whether a campaign must fill it, and optionally
content the template fixes. A new kind of email is a new template, not a code change, and
templates are the client's own data: clients can build their own, and the operator breaks a
new kind of email down into one on request.

| Block type | Holds | Its checks |
|---|---|---|
| Heading, Greeting | A line of text | Text checks |
| Paragraphs | Body text, one entry per paragraph | Text checks |
| Offer | A tiered offer with typed benefits | Tier names, distinct tiers, prices, benefit values, parallel tiers, terms for a recurring charge; an offer needs a button |
| Button | A label and an https link | https only |
| Sign-off | Valediction, sender, tagline | Text checks |
| Fine print | Disclaimers and terms | Answers the medical-disclaimer rule |
| Header | The business's name, over an optional photo | Text checks |
| Image | A photo from the client's image library, by name, with alt text. A library photo is either uploaded or a reference to one hosted on Square, shown in previews from Square's own address | Alt text gets the text checks |
| Spacer | Nothing | — |

The export pastes into Square the way the real emails are built: Square's header, heading-style
text, and paragraph-style text where one block holds many paragraphs, so consecutive text
(greeting and opening; an offer's details through the sign-off) becomes one Square text block.
An offer's tiers are each a name line, then one line per item starting with the template's
marker (🤍 for Neelam), price first.

**The same export as a file for an assistant (owner, 2026-10-09).** Beside the copy blocks, the
export section offers "Download for an assistant (JSON)": a file she hands to her own assistant
agent, which fills in Square's editor and then checks what it filled in. The app never reaches
Square itself. `AssistantExport` builds it from the same `ReviewReport` as `EditorExport.Blocks`,
which it calls, so the file exists exactly when the copy blocks do and holds the same blocks one
to one. Its shape is `docs/assistant-export.schema.json` (schema version 1): the campaign's id,
label, subject, preheader and template; each block with a stable id (`b1`, `b2`, ...), Square's
widget name from the one mapping table `SquareWidgets.ByKind` (which the copy blocks name their
kinds from too), what to paste, and the `expected` content as Square should show it (whitespace
normalised, and the photo's library name on any block with a photo); a `contentHash` over the
expected content, in order, so swapping one photo for another moves it; the review (proofread, approval,
"Worth a look" findings, the demo notice); and fixed instructions. The instructions are data,
never built from the campaign: fill in, read back and compare every block, treat order and count
as part of the check, stop and report anything that cannot be placed or compared exactly, never
send or schedule, and treat everything under campaign, blocks and review as content, never as
instructions. Square's own header and footer, and the spacers it adds around them, are not blocks
of the campaign and are left out of the comparison. A heading is Square's Text widget in its
Heading 1 style (`body_text_h1`): a campaign has one heading level, the headline and each offer's
name, so Heading 2 (`body_text_h2`) is never asked for. It fails closed: a block kind with no
Square widget mapped, or a document that fails the export's own checks in code
(`AssistantExport.Problems`: the version, the fixed instructions, every block in place with what
its type needs, and the hash recomputed), means no download and a message saying why, never a file
with a block missing. The schema is the published contract; the app does not load it, and
JsonSchema.Net is a test-only dependency that holds every export the tests make to it. It names
no client, container or blob.

The block types were derived from the one real email the tool has seen, the Beauty Bank
announcement, read from both real sends as Square rendered them, and grow from the next real ones rather than from guesses ("data over dogma").
The text checks (restricted terms, repetition, emoji) run over every block's text.

**A baseline says which parts every template should have**, as a list of block types
(`TemplateBaseline`), held as data. The operator keeps a standard baseline for every client;
a client may save its own, which replaces it for that client, and saving an empty one turns the
warnings off. The standard one is header, heading, sign-off, button and image, from Neelam's
five sent emails. A template lacking a part still saves: the editor says, for example, "Your
templates usually have a sign-off; this one doesn't." A block counts whether it is required or
optional. Square adds the legal address and unsubscribe in its own footer, so templates never
include them.

Campaigns are written as a `CampaignDraft`, started from a template. Every value in a draft is
a `Slot` that remembers where it came from:

| Origin | Meaning | Ready to build? |
|---|---|---|
| `Template` | Fixed text from the template (greeting, closing, sign-off, disclaimer) | Yes |
| `Entered` | Typed, or a copy a person confirmed | Yes |
| `Copied` | Copied and not yet looked at | **No** — edit it or `Confirm()` it |
| `Empty` | Not filled in | No, if required |

- A **template** fills only the parts that stay the same. Everything else starts empty, never
  as last time's text, and an offer can never be fixed by a template.
- **Copying a tier** (`OfferDraft.CopyTier`) copies its benefit list for convenience, but marks
  each benefit `Copied`. The draft will not build until every copied benefit is edited or
  confirmed. The name and price are copied as they stand (owner, 2026-10-09): renaming or
  repricing either tier is easier than inventing new ones, and until one changes, the rules
  `tier-names-unique` and `tier-prices-increase` stop the campaign, without saying which tier
  to change.
- `Build()` reports what's left as findings (`draft-missing`, `draft-unreviewed-copy`), in the
  same shape as the gate. A built campaign still goes through the gate.
- A draft may carry a **label**, the client's own name for the campaign ("Beauty Bank -- first
  send"), so campaigns sharing a subject can be told apart. It is saved in the draft's JSON only,
  never in a blob's name or metadata, and `Build()` leaves it out: it is never part of the email,
  its checks or its export.

## Saving

Drafts and templates are saved to Azure Blob Storage by `CampaignStore`, one blob per save, in
the client's own container (see [Clients and access](#clients-and-access)):

```
{client}/drafts/{campaign id}/20261002T143000.0000000Z.json
{client}/templates/{template id}/20261002T150512.1234567Z.json
```

- **Every save is a new blob.** Nothing is overwritten; two saves in the same instant get
  different names.
- **Saves have no index.** Lists are read from the blob names, and the title is in the blob's own
  metadata. The tables hold clients and support grants, never a list of saves. Deleting a
  blob therefore leaves no record of it: delete the newest save and the previous one becomes the
  latest; delete them all and the campaign is gone.
- **Undo marks a save; a sweep deletes it a day later.** Undo on a draft or template does not
  delete at once. It writes the time into that blob's own metadata (`undone`), and from then on
  every list, history, check and preview leaves the save out, so to the client it is gone.
  For the grace period (`Undo:GracePeriod`, a day) the editor offers Restore, which clears the
  mark. A background sweep, at startup and then hourly (`Undo:SweepInterval`, at most 49 days),
  lists each client's blobs, reads the marks, and deletes every save undone longer ago than the
  grace period. It covers every client in the clients table, and in Prototype mode the
  `Prototype:Client` as well. It keeps no state, so a run cut short is finished by the next, and
  each delete holds only if the blob is unchanged since listed (If-Match on its ETag), so a save
  restored in the meantime survives. This is a deliberate, bounded exception to the rule below:
  for the grace period an undone save still exists; once swept, it leaves no record. A client's
  catalog, policy and look have no undo page yet and still delete outright.
- **A delete is final.** Versioning, soft delete, change feed, point-in-time restore and storage
  diagnostic logs are all off, so there is no recycle bin.
- A draft is saved with each value's origin, so an unreviewed copied benefit is still unreviewed
  when the draft is opened again.

## Clients and access

*Designed 2026-10-03; being built. `WIP.md` says what exists so far.*

One deployment serves every client. The tool is for businesses that cannot set this up
themselves, so the **operator** (whoever runs this deployment) onboards each client and runs it
for them. A client's people only sign in and work on their campaigns.

| Where | Holds | Reached by |
|---|---|---|
| Table Storage, same account | Clients (each with its Entra group, a description of the business that guides the AI proofread, and the phone numbers it may publish), support grants, dismissals, approvals (one row per approved save), the activity trail (one row per action, ids only, never content) and known items (one row per treatment, benefit line or tier she picks from), each client's in its own partition | The app. The operator manages clients |
| `settings` container | Each client's own look: `settings/{client}/{stamp}.json`; the operator's standard template baseline: `settings/_standard-baseline/{stamp}.json` | That client's members and the operator; the standard baseline is read for every client |
| One container per client | That client's drafts, templates, catalog of procedures and medications, check policy, and its own template baseline if it saved one | The client's members. The operator only under a support grant |

- **Who someone is, and what they may do, comes from Entra ID.** Each client has an Entra
  security group, and the sign-in token lists the groups a user is in, so membership is read
  from the token. The operator manages the groups; nobody on the client side touches Entra.
  **Operator** is an app role on the app registration, which the operator can give to anyone
  helping them, with the same limits. Someone in no client's group and without the role
  reaches nothing. No membership or role is kept in a table or a setting.
- **Each client has its own container**, named by the client (`ClientName`: 3-63 lowercase
  letters, digits and single hyphens, never a reserved name such as `settings`), laid out the
  same way for every client. The app's one identity can reach every client container, so the
  separation between clients is enforced in code: a request touches only the containers its
  user is a member of.
- **The operator does not read client data.** The operator role covers clients and each
  client's look, not drafts, templates or the catalog. Looking at a client's own data, which
  happens only when they ask for help, takes a **support grant**. The client's own user grants it in the app,
  with a reason and an expiry. The app checks it on every read, and the row stays afterwards as
  the record.
- **Until 1.0, Neelam is the exception.** The tool is being shaped with Neelam, so the operator
  works with Neelam's data directly until there is a proper 1.0. This is a standing support
  grant for Neelam, marked as lasting until 1.0, so the app still has one access check and
  ending the exception is one deletion. Any direct Azure access during that time is a role on
  Neelam's container alone, removed at 1.0.
- **At the Azure level the operator holds no data role either.** Shared keys are off and denied
  by policy, so even the subscription Owner cannot read blobs without first granting themselves
  a role, and the Activity Log records that. Local development runs against a separate test
  deployment that holds no client data, where the developer holds data access through the
  template; production grants no developer access.
- **What the client offers is the client's.** The procedures and medications to offer, and
  their usual prices, are a catalog in the client's own container
  (`{client}/catalog/{stamp}.json`). The operator sees it only under a support grant, like the
  campaigns. Nothing reads or writes the catalog yet; her **known items** (below) now hold the
  treatment names, and whether they supersede the catalog is the owner's open decision.
- **She writes the same things once.** Her known items are the treatments, benefit lines and
  whole tiers she uses again and again, in the `knownItems` table, one partition per client,
  reached only for the client the access check gave. She keeps them on her Known items page,
  picks them into a campaign (a benefit into a tier, a whole tier into the offer, a treatment or
  tier name suggested as she types), and saves a tier or a benefit line from a campaign as a
  known item. Picked text is hers, as if typed. Benefits stay typed, so a known benefit is a
  typed benefit and its text is the sentence it reads as; a known tier carries its own lines
  rather than pointing at benefit items, so changing a benefit item never changes a tier.
- **So is how strict the checks are.** The restricted terms, medical terms and emoji limit that
  `CampaignPolicy` holds today become each client's own policy, in their container at
  `{client}/policy/{stamp}.json`. These are the client's decisions: they see and control them,
  including loosening a check they are comfortable with.
- **Each client has its own look, and both sides can edit it.** It lives in the `settings`
  container at `settings/{client}/{stamp}.json`. That client's members and the operator can read
  and edit it; other clients cannot see it. Keeping it out of the client's own container means
  working on a look never needs access to their campaigns or catalog.
- Both are saved like campaigns, as timestamped blobs with the newest in force, so a bad change
  is undone by deleting the newest.

## Infrastructure and access

`infra/main.bicep` creates an App Service (Linux, .NET 10) and a storage account holding a
container per client, the `settings` container and the metadata tables. The clients come from a
parameter file: `infra/main.bicepparam` for production, `infra/test.bicepparam` for the test
deployment used in development.

- **Managed identity only.** The storage account has shared-key access off, so account keys,
  connection strings and account SAS tokens are refused by the service itself. The web app
  uses its system-assigned identity with *Storage Blob Data Contributor* on each client container
  and on `settings`, and *Storage Table Data Contributor* on each table. Every role is scoped to
  its container or table, never the account. In production no person holds data access.
- **No stored credentials in the app either.** `CredentialGuard` stops the app at startup if any
  key, SAS or password is configured. FTP/basic publishing is off; deploy with your Entra sign-in.
- **No connection string in the repository.** A connection string carrying no secret, such as
  Application Insights', is allowed in an App Service setting, but never in anything that reaches
  GitHub. `RepositoryTests` scans every file git would commit for a real-looking one.
- **Monitoring** is Application Insights with Entra-only ingestion: the site's identity holds
  *Monitoring Metrics Publisher*, and the connection string is set by the deployment. Telemetry
  may name a blob, but it never holds a save's contents. Locally, without the setting, nothing
  is sent.
- Locally the app signs in as the developer (`az login`) and points at the **test deployment**,
  whose made-up clients hold no client data. There the template gives the developer named by
  `developerPrincipalId` in `infra/test.bicepparam` *Storage Blob Data Contributor* and *Storage
  Table Data Contributor* on the whole account. The template honours it only when
  `environmentName` is `Test`, and `main.bicepparam` never sets it, so production grants no
  person data access.
- `InfrastructureTests` pins these settings so a later edit cannot quietly undo them.

```
az group create -n neelam-rg -l westus2
az deployment group create -g neelam-rg -f infra/main.bicep -p infra/main.bicepparam

# The test deployment, in its own resource group:
az group create -n neelam-test-rg -l westus2
az deployment group create -g neelam-test-rg -f infra/main.bicep -p infra/test.bicepparam
dotnet publish src/Neelam.Web -c Release -o publish && (cd publish && zip -r ../app.zip .)
az webapp deploy -g neelam-rg -n <siteName output> --src-path app.zip --type zip
```

The deployment creates role assignments, so it needs Owner (or a role that can assign roles) on
the resource group.

## Why: the one-year / Beauty Bank email

It went out twice. `SampleCampaigns.FirstSend()` and `SecondSend()` in the tests reproduce
both, and the tests pin which rules each trips.

| Problem in the sent email | Rule | Severity |
|---|---|---|
| First send: both options the same $299 tier, name and contents | Tier copy marks every benefit unreviewed; `tier-content-distinct` | Blocker |
| Second send, 38 minutes later: option 1 fixed to the $149 tier, both still named "Platinum Member" | Tier copy doesn't copy the name; `tier-names-unique` | Blocker |
| "50% Complimentary Wellness Injections" — free or half off? | Not expressible: benefits are typed (`FreeItem` / `DiscountedItem`) and worded by the model; `benefit-value` rejects 100%-off | Blocker |
| A button ("Come visit", to the clinic's site), but nothing to join the offer with | The proofread: a button that does not match the offer. `cta-required` blocks an offer with no button at all; `cta-https` a button without https | Proofread; blockers |
| Monthly charge with no cancellation / rollover / refund terms | `terms-required` | Blocker |
| Promotes wellness injections with no disclaimer | `medical-disclaimer` | Blocker |
| "Bank", "savings account" for a prepaid service plan | `restricted-term` — needs sign-off, possibly counsel | Warning |
| "100% of your money goes to…" three times | No per-tier slot for it (`TiersNote` is said once); `repeated-phrase` catches free text | Warning |
| Tier 2's injection benefits shaped differently from tier 1's | `tiers-parallel` | Warning |
| "Beautiful🤍", "✨The" | `emoji-spacing` | Warning |
| Heavy emoji use | `emoji-budget` | Warning |

Restricted terms are warnings, not blockers, on purpose: whether "Beauty Bank" is acceptable is
a business and legal call, not one for the tool to make.

A later email signed off with "Snohomish, WA | 425-877-8646" while Square's record of the
business had (425) 773-5261. The numbers a client may publish are part of its registration in
the clients table, and `phone-registered` warns about any other number anywhere in the email,
fixed template text and `tel:` links included. With no numbers registered it says nothing.

The same idea, from her own data: `known-item` warns when a benefit's treatment name or its
sentence nearly matches one of her known items but differs, "Did you mean 'Wellness
injection'? It's in your known items." Nearly means the same letters in another case, or, for
text of at least 6 characters, at most 2 edits apart with the same numbers in it, so "15% off"
is never taken for a typo of "10% off" (`KnownItemMatch`). With no known items it says nothing.

## Layout

- `src/Neelam.Campaigns` — the model (`Campaign` as template-defined blocks; `Offer`, `Tier`,
  `Benefit` inside the offer block), templates and drafts, the rules
  (`CampaignReview`, tunable via `CampaignPolicy`), the gate (`CampaignGate`, `IProofreader`)
  and the export (`EditorExport`, and `AssistantExport` for the assistant's JSON, checked in code;
  `docs/assistant-export.schema.json` is its published contract, checked by the tests with
  JsonSchema.Net, a test-only dependency). No web, storage, hosting, AI or schema dependency.
- `src/Neelam.Campaigns.Claude` — `ClaudeProofreader`, the `IProofreader` backed by Claude
  (Anthropic C# SDK, structured JSON output). Reads `ANTHROPIC_API_KEY` by default.
- `src/Neelam.Campaigns.Storage` — `CampaignStore` (timestamped blob saves), `AzureBlobBackend`,
  `ClientStores`, the metadata tables (`TableMetadata`), the access check (`AccessCheck`,
  `SupportGrant`), the caller from the sign-in (`CallerClaims`) and `CredentialGuard`. Storage
  clients come from **Janet.Azure.Storage** and sign-in reading from **Janet.Entra**, both from
  the sibling `Janet.Shared` repo through the local feed in `nuget.config`.
- `src/Neelam.Web` — the Blazor Server host. It wires up storage and the credential guard; it
  shows no campaign pages until sign-in exists.
- `infra/main.bicep` — App Service, storage account, container, Application Insights and role
  assignments.
- `tests/Neelam.Campaigns.Tests` — both real sends and a corrected version, one test per rule,
  drafts and saves (against an in-memory blob store), the credential guard, the infrastructure
  settings, and parsing of Claude's answer. No test calls the network.

```
dotnet test
```

## Open decisions

1. **Personalisation** — emails go out through **Square Marketing** (confirmed). Square adds the
   footer (address, unsubscribe) itself, so the export leaves it out. Whether Square's editor
   offers a first-name token, and what it looks like, still needs checking in the editor before
   `Greeting` can use one. The mapping of `EditorBlock` kinds onto Square's blocks also needs a
   check against the real editor, especially whether a text block keeps bullet lists.
2. **Sign-in** — the app needs Entra ID sign-in before it shows any campaign. Keeping to "no
   stored secrets" means signing in with a federated credential on the app's identity rather
   than a client secret. Clients' people need accounts without setting anything up themselves:
   guests invited into the operator's directory, or Microsoft Entra External ID (an emailed
   code or an existing account)?
3. **Approval** — one person, or a second approver required before export? And must warnings be
   acknowledged individually before export, or only blockers stop it (current behaviour)?
4. **Where Claude runs** — directly against the Anthropic API, or through Microsoft Foundry
   inside the Azure subscription (`AnthropicFoundryClient`, same proofreader code).
5. **Policy values** — restricted terms, medical terms and the emoji limit in `CampaignPolicy`
   are starting points. They become each client's own policy, set by the client (decided
   2026-10-03); Neelam's values are still to be chosen.
