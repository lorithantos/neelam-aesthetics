# Known Promotions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A fourth kind of known item, a saved promotion, whose treatment lines point at her treatments, so a promotion she runs again is picked rather than retyped and every name, regular price and saving comes from its one source.

**Architecture:** `KnownPromotion` is a `KnownItem` stored as one more `Kind` in the existing `knownItems` table, its lines as compact JSON. Pure logic (model, rules, forms, the campaign editor's use / save / follow) lives in `src\Neelam.Campaigns`; the store and session in `src\Neelam.Campaigns.Storage`; the Razor pages only bind. The campaign model gains one optional field, `PricedTreatment.TreatmentId`, written only when set, so every saved draft and template reads and writes as before. The Fine print block is not changed.

**Tech Stack:** .NET 10, ASP.NET Core Blazor Server, Azure Table Storage (`Azure.Data.Tables`), System.Text.Json, xUnit.

**Spec:** `C:\repos\neelam-aesthetics\docs\superpowers\specs\2026-11-06-known-promotions-design.md` (approved by the owner, with decisions 8-12 of 2026-11-06; this plan argues from it). Also read `C:\repos\neelam-aesthetics\CLAUDE.md` and the "Known items" and "Promotions (answer 9a ...)" parts of `C:\repos\neelam-aesthetics\WIP.md`.

## Global Constraints

- The spec's facts and their one source, never typed twice: treatment name and regular price from her treatments (`KnownTreatment`); saving = regular price minus promotion price (`PricedTreatment.Saving`); promotion dates from the campaign's Promotion block, said once, by the "Offer valid …" sentence its export already ends with (`EditorExport.DatesText`). Nothing is added to the Fine print block for dates (spec decision 8).
- A saved promotion stores no dates, no treatment name and no regular price. A treatment line is `TreatmentId` + promotion `Price` only. Its fine print holds no validity sentence.
- No new table, no infrastructure change, no Bicep change. `InfrastructureTests` stays untouched.
- No test touches the network. In-memory fakes only: `InMemoryKnownItems` (`tests\Neelam.Campaigns.Tests\InMemoryMetadata.cs`), the app hosts `EnforcedApp`, `DemoApp`, `ProofreadDemoApp` (`tests\Neelam.Campaigns.Tests\EnforcedApp.cs` and siblings), the circuit holder `OpenCampaignPage` (`tests\Neelam.Campaigns.Tests\OpenCampaignPage.cs`).
- Older data reads unchanged: a draft or template saved before has no `treatmentId`, and one without it is written byte for byte as before. The schema stays `CampaignJson.SchemaVersion = 2`.
- Logs and activity events carry ids only, never a promotion's name, lines or fine print (as every known item today: `KnownItemsSession.LogFailure`).
- A store failure never ends her session: catch as `KnownItemsSession.IsStorageFailure` does, say it, log by id.
- Every amount in text goes through `Dollars.Text`; a form's number through `PlainNumber.Text`.
- Words the spec fixes, verbatim (drafts for Priya): "Used by Holiday filler promo. Change or remove that promotion first." / "a treatment no longer on your list" / "Added Full-Face Facial Balancing to your treatments, regularly $5,100." / "Full-Face Facial Balancing, regularly $5,100" / "no regular price yet" / "Used in: Holiday filler promo" / "Used in 2 saved promotions; they will show the new price." / "Changing it here changes it on your treatments and in every promotion that uses it." / "The email says:" / "Use a saved promotion" / "Replace this promotion with Holiday filler promo?" / "Replace the fine print too, or keep yours?" / "This campaign has no Fine print block, so the promotion's fine print wasn't added." / "This campaign's fine print is the template's own, so the promotion's fine print wasn't added." / "Save as a known promotion" / "From your treatments: Full-Face Facial Balancing is now regularly $5,400 (was $5,100)." / "Offer valid November 1 – December 31, 2026." Wording this plan adds is marked **(proposed)** and listed in Task 9 for the owner and Priya.
- Build and test only through Janet: the `mcp__janet__dotnet_check` tool with `target: "C:\repos\neelam-aesthetics\Neelam.sln"` and a `testFilter` for narrowed runs, or `janet check --target C:\repos\neelam-aesthetics\Neelam.sln` for the whole suite. Never bare `dotnet build` / `dotnet test`. `succeeded: true` is the only green.
- Every new test is mutation-checked: break the subject, see that test fail, restore, see it pass.
- Commits: on `main`, no branch, no push. Message: imperative subject, a prose body on why, a closing `Verified: ...` line, then the Co-Authored-By trailer the harness gives, verbatim. Never write an email address into a file: `RepositoryTests` refuses one.
- Decided by the owner and NOT changed here: whether known items supersede `ClientCatalog` (WIP open item 8); whether Remove asks first or is restorable. Removing a saved promotion behaves exactly as removing a tier does today.

## Review Focus

1. **A linked line whose treatment name she then edits** (typing, or a suggested fix's "Use this") must stop following that treatment, or the next open puts the old name back over hers. Test: Task 6, `A_linked_line_renamed_by_hand_or_by_a_suggested_fix_is_no_longer_linked`.
2. **A regular price lowered to or below a saved promotion's price** goes through, and that promotion must show the problem and refuse to be picked, never give a zero or negative saving. Tests: Task 4, `Lowering_a_regular_price_to_a_saved_promotion_s_price_shows_the_problem_on_that_promotion`; Task 6, `A_saved_promotion_with_a_problem_is_not_used`.
3. **Picking a saved promotion whose treatment has no regular price** must leave the line's regular price empty (Still to do says so), never fill $0. Test: Task 6, `A_line_whose_treatment_has_no_regular_price_is_left_for_her_to_fill`.
4. **A Fine print block the template fixes** keeps the template's text when a saved promotion is picked. Test: Task 6, `A_fixed_fine_print_block_is_left_as_the_template_has_it`.
5. **Opening a campaign whose linked treatment was removed, or lost its regular price,** leaves that line exactly as saved and says nothing. Test: Task 8, `A_line_whose_treatment_was_removed_or_lost_its_price_is_left_as_it_is`.

---

## Settled by the owner (2026-11-06, "Yes, take them")

- Spec decision 8: the Fine print block is unchanged; the Promotion block's own "Offer valid …" says the dates once (Task 6, `A_picked_promotion_s_dates_are_said_once_in_the_export`), and saving a promotion strips a matching leading validity sentence (Task 7, `A_leading_validity_sentence_for_the_promotion_s_dates_is_left_out_and_nothing_else`).
- Spec decision 9: a newest version approved in part is not approved and follows her list on opening (Task 8, `A_version_approved_in_part_follows_her_list_on_opening`).
- Spec decision 10: lowering a regular price to or below a saved promotion's price is allowed; the card shows the problem and the promotion cannot be picked until fixed (Task 4, Task 5 `A_saved_promotion_at_its_regular_price_shows_the_problem_on_its_card`, Task 6).
- Spec decision 11: a template-fixed Fine print block is left alone, with the spec's sentence (Task 6, `A_fixed_fine_print_block_is_left_as_the_template_has_it`).
- Spec decision 12: saving under a name her saved promotions have is refused as already in her known items (Task 7, `Saving_a_promotion_named_as_one_of_hers_says_it_is_already_known_and_a_store_failure_is_said_not_thrown`).

## Decisions this plan makes (implementation choices, the owner's to overrule)

- **Remove refusal lives in `KnownItemStoreRules.CheckRemove`**, beside `CheckAdd` / `CheckUpdate`, called by both stores, so it holds whoever calls the store. `KnownItemTable.RemoveAsync` therefore reads the partition first (the same read-then-write race as add and update).
- **"No regular price yet" is a second span**, `<span class="muted">no regular price yet</span>`, beside `KnownTreatment.Shown`, so `Shown` and the tests that read `<span>Wellness injection</span>` stay as they are.
- **`TreatmentId` is left out of what an approval is of** (`CampaignJson.SerializeContent`), as the template id is: linking changes no word of the email. It carries `[JsonIgnore]` on the model, so no model JSON changes; the draft's own JSON carries it through `CampaignJson`'s documents.
- **Linking a typed line is exact**: the cleaned text equals a treatment's name, ordinal. `SetTreatment` keeps filling the regular price ignoring case, as now.
- **Picking keeps any dates already typed** in the block; it never fills dates ("dates are left empty" read as "not taken from the saved promotion").
- **Questions are asked in turn**: "Replace this promotion ...?" first (only when the block has lines), then "Replace the fine print too, or keep yours?" (only when the editable Fine print block has text and the saved promotion has fine print). Keep mine at the first stops everything; keeping her fine print still fills the promotion.
- **The Fine print block filled is the first one the template does not fix.** A saved promotion with no fine print leaves it untouched and says nothing.
- **A saved promotion with any `KnownItemRules.Problems` is not picked**: nothing is filled and the problems are said.
- **"One of her saved ones"** (Save as a known promotion offered or not) compares name (`KnownItemMatch.IsKnown`), summary, audience and lines (treatment by id or by her name for it, promotion price, benefit by value). Fine print and dates are not compared.
- **The regular price box on the Promotions tab**: a changed amount changes her treatment; a blank box never removes the price she has.
- **A near miss on the Promotions tab or in Save as a known promotion is refused** with the line and "Did you mean 'X'? It's in your known items." (the checks' own sentence); nothing is added.
- **The strip on save** matches the Promotion block's `DatesText` with its audience or without, in any case, any spacing, and a hyphen, en or em dash; only at the start and only as a whole sentence.
- **Following her list happens on opening only** (`CampaignEdit.OnInitializedAsync`), after the checks' lists are read. Not after Load the latest, Discard, Undo or Restore: those are her actions, and the keeper would then write the followed form as a working copy.
- **The template page gets no "Use a saved promotion"**: `PromotionFields` draws the picker only when the page passes `UseSaved`, and only `CampaignEdit` does.

## Files

Create:
- `src\Neelam.Campaigns\KnownPromotionForm.cs`: the Promotions tab's form (`KnownPromotionForm`, `KnownPromotionLineForm`, `KnownTreatmentLineForm`, `KnownBenefitLineForm`, `KnownPromotionPlan`).
- `tests\Neelam.Campaigns.Tests\KnownPromotionTests.cs`: `KnownPromotionTests` (model, JSON, rules, store, session, campaign editor) and `KnownPromotionPageTests(ProofreadDemoApp)` (campaign page in a circuit).

Modify (line ranges at b544a99):
- `src\Neelam.Campaigns\KnownItems.cs:4-14` (enum), `:204-214` (beside `KnownTier`), `:217-371` (`KnownItems`, incl. `SaysTheSame` after `:268`), `:374-400` (`KnownItemRules`)
- `src\Neelam.Campaigns\CampaignJson.cs:36-42`, `:71-136` (save/restore), `:201-220` (content), `:248-255` (beside `SerializeBenefits`), `:430-435` (`PromotionLineDocument`)
- `src\Neelam.Campaigns\Campaign.cs:205-213` (`PricedTreatment`); `src\Neelam.Campaigns\CampaignDraft.cs:316-333` (`PricedTreatmentDraft`)
- `src\Neelam.Campaigns\TemplateUpdate.cs:110-147`; `src\Neelam.Campaigns\EditorExport.cs` beside `DatesText` (`:175-176`)
- `src\Neelam.Campaigns\CampaignEditor.cs:15-133` (`CampaignEditor`), `:563-658` (`PromotionEditor`), `:676-712` (`PricedTreatmentEditor`); `src\Neelam.Campaigns\SuggestedFix.cs:130-132`
- `src\Neelam.Campaigns\KnownItemsTab.cs` (whole, 30 lines)
- `src\Neelam.Campaigns.Storage\KnownItemTable.cs:30-51`, `:122-126`, `:132-177`; `src\Neelam.Campaigns.Storage\KnownItemsSession.cs:55-147`, `:193-211`, after `:278`; `src\Neelam.Campaigns.Storage\DraftSession.cs:164-169`
- `src\Neelam.Web\WorkingCopyKeeper.cs:83-94` (beside `Track`)
- `src\Neelam.Web\Components\Shared\PromotionFields.razor` (whole, 144 lines); `src\Neelam.Web\Components\Shared\KnownItemLists.razor` (whole, 30 lines)
- `src\Neelam.Web\Components\Pages\CampaignEdit.razor:190-200` (notices), `:300-301` (promotion), `:790-826` (known-item saves), `:992-1021` (`OnInitializedAsync`)
- `src\Neelam.Web\Components\Pages\KnownItemsPage.razor:43-87` (Treatments), `:126-159` (insert Promotions), `:180-186` (`Tabs`), `:208-211` (`ItemActions`)
- `tests\Neelam.Campaigns.Tests\InMemoryMetadata.cs:231-235`; `tests\Neelam.Campaigns.Tests\PromotionTests.cs` (class ends `:588`); `tests\Neelam.Campaigns.Tests\KnownItemPagesTests.cs:24-41`, `:84-90`, `:171-194`, `KnownItemsTabPageTests` `:594-611`; `tests\Neelam.Campaigns.Tests\WorkingCopyPageTests.cs:483-499`; `tests\Neelam.Campaigns.Tests\ApprovalsRequiredTests.cs` (one test)
- `C:\repos\neelam-aesthetics\WIP.md` (Task 9)

**Test fixtures** (top of `KnownPromotionTests`, `internal static` so the page tests reuse them):

```csharp
internal static readonly ClientName Salon = new("test-salon-one");
internal static readonly KnownTreatment Midface = new(KnownItem.NewId(), "Midface Lift / Cheek Rejuvenation", 2550m);
internal static readonly KnownTreatment Balancing = new(KnownItem.NewId(), "Full-Face Facial Balancing", 5100m);
internal static readonly KnownTreatment Wellness = new(KnownItem.NewId(), "Wellness injection");          // no regular price
internal static KnownPromotion Holiday(params KnownPromotionLine[] lines) => new(KnownItem.NewId(), "Holiday filler promo",
    null, null, lines.Length > 0 ? lines : [new KnownTreatmentLine(Midface.Id, 1599m), new KnownTreatmentLine(Balancing.Id, 3499m)],
    "Offer valid for filler treatments booked and performed within those dates. Not valid with other offers.");
internal static readonly KnownItems Known = new([Midface, Balancing, Wellness, Holiday()]);
// Her Filler Promo template with a Fine print block each campaign fills (PromotionTests.Template fixes it).
internal static readonly CampaignTemplate Editable = new("Filler Promo Nov / Dec",
    [.. PromotionTests.Template.Blocks.Select(b => b.Type == BlockType.FinePrint ? new TemplateBlock("Fine print", BlockType.FinePrint) : b)]);
```

`PromotionTests.FillerPromo` writes its headline text into every text block the template does not fix, so with `Editable` its Fine print block starts as "Refresh for the holidays": set the fine print in any test that reads it. `FillerPromo`'s promotion runs November 1 – December 31, 2026. In `Editable` the Promotion block's index is 1. Circuit test classes share one app per class: each test names its own treatments and promotion with a suffix of its own (`$"Full-Face Facial Balancing {n}"`).

---

### Task 1: The known promotion: model, lines JSON and rules

**Files:** Modify `src\Neelam.Campaigns\KnownItems.cs:4-14`, after `:214`, `:217-371`, `:374-400`; `src\Neelam.Campaigns\CampaignJson.cs` after `:253`. Create `tests\Neelam.Campaigns.Tests\KnownPromotionTests.cs` (fixtures above, region "Model").

**Interfaces:**
- Consumes: `KnownItem`, `KnownItemMatch.IsKnown`, `PricedTreatment.Describe()`, `Dollars.Text`, `Benefit`.
- Produces:
  - `KnownItemKind.Promotion` (last member; summary "A saved promotion: its name, its lines pointing at her treatments, and its fine print.").
  - `[JsonPolymorphic(TypeDiscriminatorPropertyName = "line")] [JsonDerivedType(typeof(KnownTreatmentLine), "treatment")] [JsonDerivedType(typeof(KnownBenefitLine), "benefit")] public abstract record KnownPromotionLine;`
  - `public sealed record KnownTreatmentLine(string TreatmentId, decimal Price) : KnownPromotionLine;` and `public sealed record KnownBenefitLine(Benefit Benefit) : KnownPromotionLine;`
  - `public sealed record KnownPromotion(string Id, string Name, string? Summary, string? Audience, IReadOnlyList<KnownPromotionLine> Lines, string? FinePrint) : KnownItem(Id)`, `Kind => KnownItemKind.Promotion`, `Text => Name`, with `public const string NoLongerOnYourList = "a treatment no longer on your list";`, `bool Uses(string treatmentId)`, `bool HasMissingTreatment(KnownItems known)`, `IReadOnlyList<string> Shown(KnownItems known)` (each line as the email says it at today's prices), `IReadOnlyList<string> LineProblems(KnownItems known)`.
  - On `KnownItems`: `IReadOnlyList<KnownPromotion> Promotions`; `KnownPromotion? PromotionNamed(string text)` (as `TierNamed`); `IReadOnlyList<KnownPromotion> PromotionsUsing(string treatmentId)`; `KnownTreatment? TreatmentById(string id)`; `KnownTreatment? TreatmentNamed(string name)` (the treatment `KnownItemMatch.IsKnown` says it is). `SaysTheSame`: two promotions are the same with the same name ignoring case and surrounding space, as tiers.
  - `public static string CampaignJson.SerializePromotionLines(IReadOnlyList<KnownPromotionLine> lines)` and `public static IReadOnlyList<KnownPromotionLine> DeserializePromotionLines(string json)` (the `Compact` options, as `SerializeBenefits`; `InvalidDataException` for empty).

- [ ] **Step 1: Write the failing tests** in `KnownPromotionTests`:

```csharp
[Fact]
public void A_saved_promotion_shows_each_line_at_today_s_prices_from_her_treatments()
{
    var known = new KnownItems([Midface, Balancing, Wellness, Holiday(
        new KnownTreatmentLine(Balancing.Id, 3499m), new KnownTreatmentLine(Wellness.Id, 99m),
        new KnownBenefitLine(new Credit(50m, "your next appointment")), new KnownTreatmentLine(KnownItem.NewId(), 10m))]);
    Assert.Equal([
        "Full-Face Facial Balancing — $3,499 (Regularly $5,100, saving $1,601)",
        "Wellness injection — $99 (no regular price yet)",                      // (proposed)
        "$50 credit toward your next appointment",
        "a treatment no longer on your list — $10"], known.Promotions.Single().Shown(known));
    var renamed = new KnownItems([Balancing with { Name = "Facial Balancing", RegularPrice = 5400m }, known.Promotions[0]]);
    Assert.StartsWith("Facial Balancing — $3,499 (Regularly $5,400, saving $1,901)", renamed.Promotions[0].Shown(renamed)[0]);
}

[Fact]
public void The_rules_hold_a_saved_promotion_to_her_treatments_and_their_regular_prices()
{
    var known = new KnownItems([Midface, Balancing, Wellness]);
    Assert.Empty(KnownItemRules.Problems(Holiday(), known));
    Assert.Equal(["Fill in the promotion's name."], KnownItemRules.Problems(Holiday() with { Name = " " }, known));
    Assert.Equal(["The promotion has no lines."], KnownItemRules.Problems(Holiday() with { Lines = [] }, known));
    Assert.Equal(["Line 1 is a treatment no longer on your list: change or remove it."],     // (proposed)
        KnownItemRules.Problems(Holiday(new KnownTreatmentLine(KnownItem.NewId(), 10m)), known));
    Assert.Equal(["'Full-Face Facial Balancing' needs a promotion price above $0."],
        KnownItemRules.Problems(Holiday(new KnownTreatmentLine(Balancing.Id, 0m)), known));
    Assert.Equal(["'Full-Face Facial Balancing' is $5,100 in the promotion but regularly $5,100: the promotion price must be below the regular price."],
        KnownItemRules.Problems(Holiday(new KnownTreatmentLine(Balancing.Id, 5100m)), known));
    Assert.Empty(KnownItemRules.Problems(Holiday(new KnownTreatmentLine(Wellness.Id, 9999m)), known));   // no regular price: above $0 only
    var saved = new KnownItems([Midface, Balancing, Holiday()]);
    Assert.Equal(["\"Holiday filler promo\" is already in your known items."],
        KnownItemRules.Problems(Holiday() with { Name = "HOLIDAY FILLER PROMO" }, saved));
}
```

- `Her_list_answers_which_saved_promotions_use_a_treatment_and_names_a_treatment_as_the_checks_do`: over `Known`, `PromotionsUsing(Balancing.Id)` is the Holiday promotion's id, `PromotionsUsing(Wellness.Id)` empty; `TreatmentNamed("full-face facial balancings")` is `Balancing`, `TreatmentNamed("Full-Face Facial Balancin")` null; `PromotionNamed("holiday filler promo")` is it; its `Kind` is `Promotion` and `Text` "Holiday filler promo".
- `A_promotion_s_lines_round_trip_through_compact_json_with_their_kind`: lines `KnownTreatmentLine(Balancing.Id, 149.50m)` and a `Credit(50m, "your next appointment")` benefit line; the JSON starts `[{"line":"treatment","treatmentId":"<id>","price":149.50},{"line":"benefit",`, has no newline, and deserializes equal (element-wise; every line is a record).

- [ ] **Step 2: Run to see them fail.** `mcp__janet__dotnet_check`, `testFilter: "FullyQualifiedName~Neelam.Campaigns.Tests.KnownPromotionTests"`. Expected: build errors naming `KnownPromotion`, `KnownTreatmentLine`, `SerializePromotionLines`.
- [ ] **Step 3: Implement the Interfaces.** `LineProblems` checks each line in order and gives its first failing check, in the tests' sentences (the price ones are the campaign check's own, `CampaignReview.PromotionPrices`). `KnownItemRules.Problems` adds `case KnownPromotion p:` the name and no-lines sentences, then `p.LineProblems(existing)`. `Shown` formats as the first test reads, a priced line through `PricedTreatment.Describe()`.
- [ ] **Step 4: Run to see them pass.** Same filter. Expected: `succeeded: true`.
- [ ] **Step 5: Mutation-check.** Read the regular price from the line's own price in `Shown`; drop the `>=` check; make `TreatmentNamed` ordinal; drop the discriminator name. Each fails its test; restore.
- [ ] **Step 6: Run the whole suite.** `janet check --target C:\repos\neelam-aesthetics\Neelam.sln`. Expected: exit 0.
- [ ] **Step 7: Commit:** "Known items gain a saved promotion whose lines point at her treatments"

---

### Task 2: Campaign model: a line's TreatmentId

**Files:** Modify `src\Neelam.Campaigns\Campaign.cs:205-213`, `src\Neelam.Campaigns\CampaignDraft.cs:316-333`, `src\Neelam.Campaigns\CampaignJson.cs:36-42`, `:71-136`, `:201-220`, `:430-435`, `src\Neelam.Campaigns\TemplateUpdate.cs:110-147`. Test: `tests\Neelam.Campaigns.Tests\PromotionTests.cs` (new region "Linked lines").

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces:
  - `public sealed record PricedTreatment(string Treatment, decimal Price, decimal RegularPrice, [property: JsonIgnore] string? TreatmentId = null) : PromotionLine` (doc: "the known treatment the line came from; null for a line typed by hand or saved before 2026-11-06").
  - `PricedTreatmentDraft.TreatmentId { get; set; }` (`string?`); `ToLine()` passes it.
  - `PromotionLineDocument` gains `string? TreatmentId = null` (left out when null). `SerializeDraft(d, content)` threads `content` into `Save(PromotionDraft, bool content)`, which writes `TreatmentId: content ? null : t.TreatmentId`; `Restore` sets it back; `AsTyped` keeps it.
  - `TemplateUpdate.Take` copies a promotion line's `TreatmentId`.

- [ ] **Step 1: Write the failing tests** in `PromotionTests` (each sets `((PricedTreatmentDraft)editor.Draft.Promotion("Promotion").Lines[i]).TreatmentId` on `FillerPromo(template: KnownPromotionTests.Editable)`):
  - `A_linked_line_round_trips_through_the_draft_s_json`: line 1 linked to `Balancing.Id`; the JSON contains `"treatmentId": "<Balancing.Id>"`; deserialized, line 1's `TreatmentId` is `Balancing.Id`; serializing the read-back draft gives the same JSON.
  - `Without_a_link_drafts_and_templates_are_written_as_before`: `SerializeDraft(FillerPromo().Draft)` and `SerializeTemplate(Template)` contain no `treatmentId`; the read-back line 0's `TreatmentId` is null.
  - `Linking_a_line_is_not_part_of_what_an_approval_is_of`: `SerializeContent` is equal before and after linking line 0 to `Midface.Id`.
  - `A_template_update_keeps_a_line_s_link`: line 0 linked to `Midface.Id`; `TemplateUpdate.Of(editor.Draft, Editable, Guid.NewGuid(), DateTimeOffset.UnixEpoch).Draft`'s line 0 has `Midface.Id`.
- [ ] **Step 2: Run to see them fail.** `testFilter: "FullyQualifiedName~Neelam.Campaigns.Tests.PromotionTests"`. Expected: build errors naming `TreatmentId`.
- [ ] **Step 3: Implement the Interfaces.**
- [ ] **Step 4: Run to see them pass.** Same filter, then `FullyQualifiedName~TemplateUpdateTests|FullyQualifiedName~TemplateDefaultTests|FullyQualifiedName~TemplateLinkTests`. Expected: `succeeded: true` both.
- [ ] **Step 5: Mutation-check.** Drop `content ? null :` (approval test fails); write `treatmentId: null` instead of leaving it out ("as before" fails); skip the copy in `TemplateUpdate`. Restore each.
- [ ] **Step 6: Run the whole suite.** Expected: exit 0.
- [ ] **Step 7: Commit:** "A promotion line can name the known treatment it came from"

---

### Task 3: Storage: Promotion rows, and a treatment in use is not removed

**Files:** Modify `src\Neelam.Campaigns.Storage\KnownItemTable.cs:30-51`, `:122-126`, `:132-177`; `tests\Neelam.Campaigns.Tests\InMemoryMetadata.cs:231-235`; `src\Neelam.Campaigns.Storage\KnownItemsSession.cs:193-211`. Test: `KnownPromotionTests` (region "Store").

**Interfaces:**
- Consumes: Task 1's `KnownPromotion`, `KnownItems.PromotionsUsing`, `CampaignJson.SerializePromotionLines` / `DeserializePromotionLines`.
- Produces:
  - Row mapping: `Kind` "Promotion", `Text` = name, `Lines` (compact JSON, required), `Summary`, `Audience`, `FinePrint` (each only when not null). Malformed, so left out by `ReadAll` and logged by its key as today: `Lines` missing, not parsing, holding a null line, or a treatment line with a blank id. In `ToItem`, `nameof(KnownItemKind.Promotion) => Promotion(row)`, a private reader beside `BenefitLine` throwing `InvalidDataException`.
  - `public static void KnownItemStoreRules.CheckRemove(KnownItems existing, string id)`: throws `InvalidOperationException` "Used by {names}. Change or remove that promotion first." for one, "... those promotions first." for more (proposed), names in list order joined "A", "A and B", "A, B and C". Nothing for an id not in use.
  - `KnownItemTable.RemoveAsync` and `InMemoryKnownItems.RemoveAsync` read the partition, call `CheckRemove`, then delete.
  - `KnownItemsSession.RemoveAsync` catches `ArgumentException or InvalidOperationException` as `Refuse(ex.Message)`, records nothing, keeps the item.

- [ ] **Step 1: Write the failing tests** in `KnownPromotionTests`:
  - `A_promotion_round_trips_through_its_table_row`: `Holiday() with { Summary = "Two months to refresh.", Audience = "new clients" }` through `KnownItemTable.FromItem(Salon, ...)`: `Kind` "Promotion", `Text` "Holiday filler promo", `Lines` equals `SerializePromotionLines(lines)`, `Summary`, `Audience`, `FinePrint` as given; `ToItem` gives a `KnownPromotion` equal in name, summary, audience, fine print and lines.
  - `A_promotion_with_no_summary_audience_or_fine_print_has_no_such_columns`: `Holiday() with { FinePrint = null }`: no `Summary`, `Audience` or `FinePrint` key.
  - `A_promotion_row_that_cannot_be_read_is_left_out_and_logged_by_its_key_alone`: four rows `Kind` "Promotion", `Text` "Secret", `Lines` null / `{not json` / `[null]` / `[{"line":"treatment","treatmentId":"","price":1}]`, beside `Balancing`'s row: `ReadAll` gives `[Balancing.Id]`, a `ListLogger<KnownItemTable>` has 4 entries, none containing "Secret".
  - `A_promotion_is_kept_by_the_store_only_with_its_treatments_on_her_list`: `InMemoryKnownItems.AddAsync(Salon, Holiday())` throws `ArgumentException` containing "a treatment no longer on your list"; after adding `Midface` and `Balancing` it is kept, its lines as given.
  - `Removing_a_treatment_a_saved_promotion_uses_is_refused_naming_it`: store with `Midface`, `Balancing`, `Wellness`, `Holiday()`, a session on `new TestRecords(new ManualClock(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)))`: `RemoveAsync(Balancing)` gives `Errors` `["Used by Holiday filler promo. Change or remove that promotion first."]`, `Balancing` still there, no activity event; `RemoveAsync(Wellness)` removes it with "Removed \"Wellness injection\". Campaigns that used it keep their text."
  - `The_store_rules_name_every_promotion_that_uses_a_treatment`: a second `Holiday() with { Id = KnownItem.NewId(), Name = "Spring refresh" }`: `CheckRemove(..., Balancing.Id)` says "Used by Holiday filler promo and Spring refresh. Change or remove those promotions first." (proposed).
- [ ] **Step 2: Run to see them fail.** `testFilter: "FullyQualifiedName~Neelam.Campaigns.Tests.KnownPromotionTests"`. Expected: row tests fail ("of no known kind"), remove tests fail (the treatment is removed).
- [ ] **Step 3: Implement the Interfaces.**
- [ ] **Step 4: Run to see them pass.** Same filter, then `FullyQualifiedName~KnownItemsTests|FullyQualifiedName~KnownItemPagesTests|FullyQualifiedName~ActivityTests` (the covering tests of `KnownItemsSession.RemoveAsync`). Expected: `succeeded: true`.
- [ ] **Step 5: Mutation-check.** Skip `CheckRemove` in the in-memory store; write `Lines` without `Compact`; accept a blank treatment id. Each fails its test; restore.
- [ ] **Step 6: Run the whole suite.** Expected: exit 0.
- [ ] **Step 7: Commit:** "Saved promotions are kept in the known items table, and a treatment one uses cannot be removed"

---

### Task 4: Known items session: promotions, with her list as the one source

**Files:** Create `src\Neelam.Campaigns\KnownPromotionForm.cs`. Modify `src\Neelam.Campaigns.Storage\KnownItemsSession.cs:55-147`. Test: `KnownPromotionTests` (region "Session").

**Interfaces:**
- Consumes: Tasks 1 and 3; `BenefitEditor.Standalone`, `FormText.Need` / `FormText.Money` / `FormText.Clean`, `KnownItems.NearMiss(KnownItemKind.Treatment, text)`.
- Produces (in `KnownPromotionForm.cs`):
  - `public sealed class KnownPromotionForm`: `string Name`, `Summary`, `Audience`, `FinePrint` (`{ get; set; } = ""`); `IReadOnlyList<KnownPromotionLineForm> Lines`; `static KnownPromotionForm Of(KnownPromotion promotion, KnownItems known)`; `KnownTreatmentLineForm AddTreatment()`; `KnownBenefitLineForm AddBenefit()`; `KnownBenefitLineForm AddKnown(KnownBenefit benefit)`; `void RemoveLine(KnownPromotionLineForm line)`; `public const string FinePrintHelp = "No dates here: each campaign's Promotion block says them.";` (proposed); `(KnownPromotionPlan? Plan, IReadOnlyList<string> Errors) ToPlan(string id, KnownItems known)`.
  - `public abstract class KnownPromotionLineForm`; `public sealed class KnownTreatmentLineForm : KnownPromotionLineForm`: `string Treatment { get; }`, `void SetTreatment(string text, KnownItems known)` (fills an empty `RegularPrice` from `known.TreatmentNamed(text)`), `string Price`, `string RegularPrice`, `string? Sentence(KnownItems known)` (`PricedTreatment.Describe()` once the treatment and both prices are there), `public const string RegularPriceHelp = "Changing it here changes it on your treatments and in every promotion that uses it.";`. `public sealed class KnownBenefitLineForm(BenefitEditor benefit) : KnownPromotionLineForm` with `BenefitEditor Benefit`.
  - `public sealed record KnownPromotionPlan(KnownPromotion Promotion, IReadOnlyList<KnownTreatment> Added, IReadOnlyList<KnownTreatment> Repriced)`.
- Produces (on `KnownItemsSession`): `KnownPromotionForm NewPromotion`, `KnownPromotionForm? EditPromotion`, `Task<bool> AddPromotionAsync(CancellationToken ct = default)`; `StartEdit` / `CancelEdit` / `SaveEditAsync` gain `case KnownPromotion:` before `default`; `string? Reach(KnownTreatment treatment)`.

`ToPlan` per treatment line, in order: blank name, "Line {n}: fill in the treatment, or remove the line." (proposed); `known.TreatmentNamed(name)` is that treatment; else a near miss, "Line {n}: Did you mean '{near}'? It's in your known items."; else a new `KnownTreatment(NewId, name, regular?)` into `Added` (one per name, ignoring case). Price: `FormText.Money(Price, "the promotion price", errors)` prefixed "Line {n}: ". A regular price box that parses to an amount other than the existing treatment's goes into `Repriced`; blank changes nothing. A benefit line with an `Error` or no `Value`: "Line {n}: fill it in, or remove it." (proposed). Fine print, summary, audience: cleaned, null when blank. `Of` shows a missing treatment's line as `Treatment = KnownPromotion.NoLongerOnYourList`, keeping its id, so `ToPlan` reports it through `KnownItemRules` rather than as a new name.

The session applies a plan in this order, so nothing is written for a promotion that would be refused: (1) `KnownItemRules.Problems(plan.Promotion, provisional)`, `provisional` being her items with `Repriced` replacing theirs and `Added` added; any problem refuses; (2) each `Added` through `_store.AddAsync` (`KnownItemAdded`); (3) each `Repriced` through `_store.UpdateAsync` (`KnownItemChanged`); (4) the promotion through the existing `ChangeAsync`. Message: each "Added {name} to your treatments, regularly {Dollars}." (or "Added {name} to your treatments."), each "Changed {name}'s regular price to {Dollars}." (proposed), then the existing "Added \"{name}\"." / "Changed \"{name}\".", joined by spaces. `Reach`: none, null; one, "Used in 1 saved promotion; it will show the new price." (proposed); more, "Used in {n} saved promotions; they will show the new price." Every store call stays inside the existing catch shape.

- [ ] **Step 1: Write the failing tests** in `KnownPromotionTests` (helper `Session(params KnownItem[] items)` returning `(session, store, records)` over an `InMemoryKnownItems` and a `TestRecords` trail). Each builds on `session.NewPromotion` with `Name = "Holiday filler promo"` and lines through `AddTreatment()` + `SetTreatment(text, session.Items)`:
  - `A_promotion_built_from_her_treatments_is_saved_with_its_lines_by_id`: over `Midface, Balancing`, "Full-Face Facial Balancing" fills `RegularPrice` "5100"; `Price = "3,499"`; `Sentence` is "Full-Face Facial Balancing — $3,499 (Regularly $5,100, saving $1,601)"; `AddPromotionAsync` true; saved lines `[KnownTreatmentLine(Balancing.Id, 3499m)]`; message "Added \"Holiday filler promo\"."
  - `A_new_treatment_named_on_the_promotion_form_joins_her_list_with_its_regular_price`: over `Midface`, "Full-Face Facial Balancing" price "3499" regular "5,100", and "Lip filler" price "499": message starts "Added Full-Face Facial Balancing to your treatments, regularly $5,100. Added Lip filler to your treatments."; Balancing at 5100m, Lip filler with no price; line 0 points at the new Balancing's id; 3 `KnownItemAdded` events.
  - `A_name_her_list_has_ignoring_case_and_plural_is_that_treatment_and_a_near_miss_is_refused`: over `Balancing`, "full-face facial balancings" at "3499" and "Full-Face Facial Balancin" at "100": refused with `["Line 2: Did you mean 'Full-Face Facial Balancing'? It's in your known items."]`, store still one item; after `RemoveLine` of the near miss, saved as `[KnownTreatmentLine(Balancing.Id, 3499m)]`.
  - `Changing_the_regular_price_on_the_promotion_form_changes_her_treatment_everywhere`: over `Midface, Balancing` and a saved "Spring refresh" (`Holiday() with { Id = KnownItem.NewId(), Name = "Spring refresh" }`), regular "5400": message contains "Changed Full-Face Facial Balancing's regular price to $5,400."; Balancing at 5400m; Spring refresh shows "Full-Face Facial Balancing — $3,499 (Regularly $5,400, saving $1,901)".
  - `A_refused_promotion_adds_no_treatment_and_changes_no_price`: over `Balancing`, price "6000" regular "5400", plus "Lip filler" at "499": refused with `["'Full-Face Facial Balancing' is $6,000 in the promotion but regularly $5,400: the promotion price must be below the regular price."]`; the store holds `[Balancing]` unchanged; no activity event.
  - `A_rename_or_price_change_on_her_list_shows_in_every_saved_promotion_and_says_how_far_it_reaches`: over `Midface, Balancing, Wellness, Holiday()`, Spring refresh: `Reach(Balancing)` "Used in 2 saved promotions; they will show the new price.", `Reach(Wellness)` null; editing Balancing to "Facial Balancing" at "5400" saves and every promotion shows "Facial Balancing — $3,499 (Regularly $5,400, saving $1,901)".
  - `Lowering_a_regular_price_to_a_saved_promotion_s_price_shows_the_problem_on_that_promotion` (Review Focus 2, decision 10): over `Midface, Balancing, Holiday()`, editing Balancing's regular price to "3499": `SaveEditAsync` true; the promotion's `LineProblems` is `["'Full-Face Facial Balancing' is $3,499 in the promotion but regularly $3,499: the promotion price must be below the regular price."]`.
  - `A_saved_promotion_is_changed_and_removed_as_a_tier_is`: `StartEdit(holiday)`: `EditPromotion.Name` "Holiday filler promo", line 1's `RegularPrice` "5100"; renamed "Holiday filler promo 2026", saved: "Changed \"Holiday filler promo 2026\"."; removed: "Removed \"Holiday filler promo 2026\". Campaigns that used it keep their text."; events `[KnownItemChanged, KnownItemRemoved]`, each `EntityId` the promotion's id.
  - `A_line_to_a_treatment_removed_meanwhile_shows_as_no_longer_on_her_list_and_is_not_saved_unchanged`: `store.PutRow(KnownItemTable.FromItem(Salon, Holiday(new KnownTreatmentLine(Balancing.Id, 3499m))))` with no Balancing; `StartEdit`: line 0's `Treatment` is `NoLongerOnYourList`; `SaveEditAsync` false with `["Line 1 is a treatment no longer on your list: change or remove it."]`.
- [ ] **Step 2: Run to see them fail.** Filter `FullyQualifiedName~Neelam.Campaigns.Tests.KnownPromotionTests`. Expected: build errors naming `NewPromotion`, `KnownTreatmentLineForm`, `Reach`.
- [ ] **Step 3: Implement the Interfaces.**
- [ ] **Step 4: Run to see them pass.** Same filter, then `FullyQualifiedName~KnownItemsTests|FullyQualifiedName~KnownItemPagesTests`. Expected: `succeeded: true`.
- [ ] **Step 5: Mutation-check.** Apply without the provisional check; treat a near miss as new; let a blank regular box clear the price; drop `Repriced`. Each fails its test; restore.
- [ ] **Step 6: Run the whole suite.** Expected: exit 0.
- [ ] **Step 7: Commit:** "Her Known items session builds, changes and removes saved promotions, and keeps her treatments the one source"

---

### Task 5: Known items page: Treatments tab and the Promotions tab

**Files:** Modify `src\Neelam.Campaigns\KnownItemsTab.cs` (`Promotions = "promotions"`; `All = [Treatments, Promotions, BenefitLines, Tiers, Ladders]`); `src\Neelam.Web\Components\Shared\KnownItemLists.razor` (`public const string Promotions = "known-promotions";`, a datalist of `Known.Promotions` names); `src\Neelam.Web\Components\Pages\KnownItemsPage.razor:43-87`, `:126`, `:180-186`, `:208-211`. Test: `tests\Neelam.Campaigns.Tests\KnownItemPagesTests.cs`, `KnownItemsTabPageTests`.

**Interfaces:**
- Consumes: Task 4's session members and forms; Task 1's `Shown`, `PromotionsUsing`; `KnownItemRules.Problems`.
- Produces (markup the tests read):
  - Tabs: `(KnownItemsTab.Promotions, $"Promotions ({items.Promotions.Count})")` second; the branch is `else if (tab == KnownItemsTab.Promotions)` before `BenefitLines`.
  - Treatments: after `<span>@item.Shown</span>`, one space, then `<span class="muted">no regular price yet</span>` when `RegularPrice is null`; `<span class="muted" data-testid="used-in-@item.Id">Used in: @names</span>` (names joined ", ") when used; in the edit form `<p class="field-help" data-testid="treatment-reach">@_session.Reach(t)</p>` when not null.
  - `ItemActions` buttons gain `data-testid="change-@item.Id"` and `data-testid="remove-@item.Id"` (labels unchanged).
  - Promotions tab: `<h2>Promotions</h2>`, `<section class="card" aria-label="Promotions">`, `<h3>Build a promotion</h3>`, `PromotionFormFields(_session.NewPromotion, "new")`, `<button class="button primary" data-testid="add-promotion">Add promotion</button>`; "No promotions yet." when none; per saved promotion `<section class="card tier" aria-label="@p.Name">`, `<h3>@p.Name</h3>`, a `<ul>` of `p.Shown(items)`, `<p data-testid="promotion-fine-print-@p.Id">@p.FinePrint</p>` when it has one, `<Problems Messages="KnownItemRules.Problems(p, items)" />`, `@ItemActions(p)`; while editing, the form and `@EditActions` (the tier's pattern).
  - `PromotionFormFields(KnownPromotionForm form, string id)`: inputs `promotion-name-@id` ("Promotion name"), `promotion-summary-@id` ("Summary (optional)"), `promotion-audience-@id` ("Who it is for (optional)"); per treatment line `l`: `known-line-treatment-@id-l@l` (list `known-treatments`, `@oninput` to `SetTreatment`), `known-line-price-@id-l@l` ("Promotion price, in dollars"), `known-line-regular-@id-l@l` ("Regular price, in dollars", help `RegularPriceHelp`), `<p class="field-help" data-testid="known-line-sentence-@id-l@l">The email says: @sentence</p>` once `Sentence` is not null; a benefit line through `<BenefitFields>` as `TierFields`; buttons `promotion-add-treatment-@id` ("Add a treatment"), `promotion-add-benefit-@id` ("Type a benefit"), the "Pick a benefit" `KnownItemPicker` as in `TierFields`, "Remove line" per line; `<textarea id="promotion-fine-print-@id">` ("Fine print (optional)") with `<p class="field-help" data-testid="promotion-fine-print-help-@id">@KnownPromotionForm.FinePrintHelp</p>`. (Labels proposed.)

- [ ] **Step 1: Write the failing tests.** In `KnownItemPagesTests`: in the one-time seed, add `new KnownPromotion(KnownItem.NewId(), "Holiday filler promo", null, null, [new KnownTreatmentLine(BalancingId, 3499m)], "Not valid with other offers.")`, `BalancingId` a `static` field holding the seeded "Full-Face Facial Balancing"'s id. Add `("promotions", "aria-label=\"Promotions\"", "Promotions (1)")` second in `Kinds` and `[InlineData("promotions")]` to `Each_tab_draws_only_its_own_kind`; in `Another_client_s_items_are_never_shown` add `("promotions", "No promotions yet.")`, `"Promotions (0)"` and `Assert.DoesNotContain("Holiday filler promo", page)`. New:
  - `The_treatments_tab_shows_each_price_or_none_and_where_saved_promotions_use_it`: `/known-items` contains `<span>Wellness injection</span> <span class="muted">no regular price yet</span>` and `<span>Full-Face Facial Balancing, regularly $5,100</span>`; `used-in-{BalancingId}` is "Used in: Holiday filler promo".
  - `The_promotions_tab_shows_each_saved_promotion_at_today_s_prices_after_the_way_to_build_one`: `/known-items?show=promotions` contains `<h3>Holiday filler promo</h3>`, `<li>Full-Face Facial Balancing — $3,499 (Regularly $5,100, saving $1,601)</li>`, "Not valid with other offers."; `promotion-fine-print-help-new` is `FinePrintHelp`; "Add promotion</button>" comes before the card's `<h3>`.
  - `KnownItemsTab.All` is `["treatments", "promotions", "benefit-lines", "tiers", "ladders"]` and `KnownItemsTab.Chosen("promotions", null)` is "promotions".

  In `KnownItemsTabPageTests` (DemoApp circuit, `OpenCampaignPage.OpenAtAsync<KnownItemsPage>(app, "known-items?show=promotions", ...)`, seeding through `app.KnownItems` with a suffix per test):
  - `A_promotion_is_built_on_the_page_and_says_what_joined_her_treatments`: type "Built here", add a treatment, "Brand-new peel 1" at "99" regular "150": `known-line-sentence-new-l0` is "The email says: Brand-new peel 1 — $99 (Regularly $150, saving $51)"; after `add-promotion` the page contains "Added Brand-new peel 1 to your treatments, regularly $150." and `<h3>Built here</h3>`.
  - `Changing_a_used_treatment_says_how_far_it_reaches_and_removing_it_says_which_promotion_uses_it`: "Reach test 2" at 5100m used by "Reach promo 2" at 10m: `remove-{id}` shows "Used by Reach promo 2. Change or remove that promotion first."; `change-{id}` shows `treatment-reach` "Used in 1 saved promotion; it will show the new price."
  - `A_saved_promotion_at_its_regular_price_shows_the_problem_on_its_card` (decision 10): "Card problem 3" at 5100m, "Card promo 3" at 3499m, then `app.KnownItems.UpdateAsync` the treatment to 3499m: the "Card promo 3" card contains "'Card problem 3' is $3,499 in the promotion but regularly $3,499: the promotion price must be below the regular price."
- [ ] **Step 2: Run to see them fail.** Filter `FullyQualifiedName~KnownItemPagesTests|FullyQualifiedName~KnownItemsTabPageTests`. Expected: the new tests and the updated `Kinds` theory fail.
- [ ] **Step 3: Implement the markup above.**
- [ ] **Step 4: Run to see them pass.** Same filter. Expected: `succeeded: true`.
- [ ] **Step 5: Mutation-check.** Put Promotions last in `All`; drop the used-in span; skip the reach line; drop `<Problems>` from the card. Each fails its test; restore.
- [ ] **Step 6: Run the whole suite.** Expected: exit 0.
- [ ] **Step 7: Commit:** "The Known items page gains a Promotions tab, and treatments say their price and where promotions use them"

---

### Task 6: Campaign: "Use a saved promotion", and lines linked to her treatments

**Files:** Modify `src\Neelam.Campaigns\CampaignEditor.cs:15-133`, `:563-658`, `:676-712`; `src\Neelam.Campaigns\SuggestedFix.cs:130-132`; `src\Neelam.Web\Components\Shared\PromotionFields.razor`; `src\Neelam.Web\Components\Pages\CampaignEdit.razor:300-301` and `@code`. Test: `KnownPromotionTests` (region "Using a saved promotion"), `KnownPromotionPageTests`, `tests\Neelam.Campaigns.Tests\WorkingCopyPageTests.cs`.

**Interfaces:**
- Consumes: Tasks 1 and 2, Task 5's `KnownItemLists.Promotions`, `KnownItemPicker<TItem>`, `KnownItemRules.Problems`.
- Produces:
  - `PricedTreatmentEditor.TreatmentId => Line.TreatmentId`; `SetTreatment(text, known)` also sets `Line.TreatmentId` to the treatment whose name equals `FormText.Clean(text)` ordinal, else null; `public void Rename(string text)` sets the text and clears the link (`SuggestedFix`'s setter at `:131` becomes `v => t.Rename(v)`).
  - `public void PromotionEditor.Use(KnownPromotion saved, KnownItems known)`: every line removed, then name, summary, audience and, per saved line, a treatment line (`TreatmentId`, her list's name, her regular price as `PlainNumber.Text` or empty, the saved price) or a benefit line (`BenefitEditor.Use`). Dates untouched.
  - `public sealed record PromotionAsks(string? ReplacePromotion, string? ReplaceFinePrint);` and `public PromotionAsks CampaignEditor.AsksBeforeUsing(PromotionBlockEditor block, KnownPromotion saved)`: "Replace this promotion with {saved.Name}?" when the block has lines; "Replace the fine print too, or keep yours?" when the first editable Fine print block has text and `saved.FinePrint` is not null.
  - `public sealed record PromotionUse(bool Used, IReadOnlyList<string> Said);` and `public PromotionUse CampaignEditor.UsePromotion(PromotionBlockEditor block, KnownPromotion saved, KnownItems known, bool replaceFinePrint)`. When `KnownItemRules.Problems(saved, known)` has any, nothing is filled: `Said` = "{saved.Name} can't be used until it is fixed on the Known items page." (proposed), then each problem. Otherwise `block.Promotion.Use(...)`, then with `saved.FinePrint` not null: into the first Fine print block the template does not fix, when `replaceFinePrint` or it is empty, the text as is; no Fine print block, "This campaign has no Fine print block, so the promotion's fine print wasn't added."; only fixed ones, "This campaign's fine print is the template's own, so the promotion's fine print wasn't added."
  - `PromotionFields`: `[Parameter] public EventCallback<KnownPromotion> UseSaved { get; set; }`; first in its markup, when `UseSaved.HasDelegate && Known.Promotions.Count > 0`, `<KnownItemPicker TItem="KnownPromotion" Id="promotion-use-saved-@Index" Label="Use a saved promotion" List="@KnownItemLists.Promotions" Placeholder="Start typing to search your saved promotions" Find="Known.PromotionNamed" Picked="p => UseSaved.InvokeAsync(p)" Help="Choosing one fills in its name, lines and fine print; you give it its dates." />` (placeholder and help proposed).
  - `CampaignEdit`: passes `UseSaved="p => AskToUse(promotionBlock, p)"`; holds `(PromotionBlockEditor Block, KnownPromotion Saved, bool ReplaceFinePrint)? _using` and renders under `<PromotionFields>`, in turn: `<p role="alert" data-testid="promotion-replace-question">` with buttons `promotion-replace` ("Replace it") / `promotion-keep` ("Keep mine"); then `fine-print-question` with `fine-print-replace` ("Replace the fine print") / `fine-print-keep` ("Keep mine") (button words proposed); the use's `Said` in `<p role="status" data-testid="promotion-use-message">`. No questions: used at once. Not through `ActingAsync`: like typing, the keeper sees the change after the render and tells her other tab (owner's every-action rule).

- [ ] **Step 1: Write the failing tests.** In `KnownPromotionTests` (`block` = the single `PromotionBlockEditor`, `fine` = the single Fine print `TextBlockEditor`):
  - `Using_a_saved_promotion_fills_its_parts_with_linked_lines_at_today_s_prices_and_leaves_the_dates`: `CampaignEditor.Start(Editable)`, `UsePromotion(block, Known.Promotions[0], Known, replaceFinePrint: false)`: `Used`, `Said` empty; name "Holiday filler promo"; lines `(TreatmentId, Treatment, Price, RegularPrice)` are `(Midface.Id, "Midface Lift / Cheek Rejuvenation", "1599", "2550")`, `(Balancing.Id, "Full-Face Facial Balancing", "3499", "5100")`; `Starts` and `Ends` empty and `Status().Missing` has "Promotion › Starts"; `fine.Field.Text` is the saved fine print.
  - `A_picked_promotion_s_dates_are_said_once_in_the_export` (decision 8): `PromotionTests.FillerPromo(template: Editable)`, used with `replaceFinePrint: true`: `EditorExport.Preview(editor.Draft.Build().Campaign!)` holds "Offer valid November 1 – December 31, 2026." exactly once, and the saved fine print.
  - `It_asks_before_replacing_her_lines_or_her_fine_print_and_not_otherwise`: on a fresh `Editable` editor `AsksBeforeUsing` is `new PromotionAsks(null, null)`; after `AddTreatment()` and fine print "Mine.", it is `("Replace this promotion with Holiday filler promo?", "Replace the fine print too, or keep yours?")`; using with `replaceFinePrint: false` leaves "Mine.".
  - `With_no_fine_print_block_it_says_the_fine_print_was_not_added`: `Editable` without its Fine print block, `replaceFinePrint: true`: `Said` is `["This campaign has no Fine print block, so the promotion's fine print wasn't added."]`.
  - `A_fixed_fine_print_block_is_left_as_the_template_has_it` (Review Focus 4, decision 11): `CampaignEditor.Start(PromotionTests.Template)`, `replaceFinePrint: true`: the fine print still starts "Offer valid for filler treatments booked and performed within those dates."; `Said` is `["This campaign's fine print is the template's own, so the promotion's fine print wasn't added."]`.
  - `A_line_whose_treatment_has_no_regular_price_is_left_for_her_to_fill` (Review Focus 3): over `[Wellness, Holiday(new KnownTreatmentLine(Wellness.Id, 99m))]`: line 0's `RegularPrice.Text` is ""; `Status().Missing` has "Promotion › Line 1 › Regular price".
  - `A_saved_promotion_with_a_problem_is_not_used` (Review Focus 2, decision 10): over `[Midface, Holiday()]`: `(Used, Said)` is `(false, ["Holiday filler promo can't be used until it is fixed on the Known items page.", "Line 2 is a treatment no longer on your list: change or remove it."])` and the block has no lines; over `[Midface, Balancing with { RegularPrice = 3499m }, Holiday()]`: not used, `Said[1]` is "'Full-Face Facial Balancing' is $3,499 in the promotion but regularly $3,499: the promotion price must be below the regular price."
  - `Editing_what_was_filled_never_changes_the_saved_promotion`: after use, line 0's price "1" and name "Changed": the saved promotion's line 0 price is still 1599m and its name "Holiday filler promo".
  - `A_line_typed_with_exactly_her_treatment_s_name_is_linked_and_any_other_is_not`: `SetTreatment("Full-Face Facial Balancing", Known)` links `Balancing.Id`; `SetTreatment("full-face facial balancing", Known)` unlinks it, and `RegularPrice.Text` is still "5100".
  - `A_linked_line_renamed_by_hand_or_by_a_suggested_fix_is_no_longer_linked` (Review Focus 1): after use, `SetTreatment("Midface Lift", Known)` on line 0 clears its link; `SuggestedFix.Apply(editor, new Finding(Severity.Warning, "ai-spelling", "Promotion", "Spelling.", Excerpt: "Full-Face Facial Balancing") { Suggestion = "Full Face Balancing" })` is `FixOutcome.Applied` and line 1's link is null (`Finding` at `src\Neelam.Campaigns\Finding.cs:15-47`).

  In `KnownPromotionPageTests(ProofreadDemoApp app)` (seed per test with suffixed names; `OpenCampaignPage.OpenAsync(app, id, FakeProofreader.Clean)`):
  - `Picking_a_saved_promotion_fills_the_block_and_the_fine_print_with_the_dates_left_empty`: an empty campaign from `Editable`; `TypeIntoAsync("promotion-use-saved-1", name)`: `promotion-name-1` holds the name, `line-sentence-1-l0` reads "The email says: ... — $1,599 (Regularly $2,550, saving $951)", the fine print textarea holds the saved fine print, Still to do has "Promotion › Starts".
  - `Picking_over_her_lines_asks_first_and_keep_mine_changes_nothing`: saved `FillerPromo(template: Editable)`; pick: `promotion-replace-question` contains "Replace this promotion with <name>?"; `promotion-keep`: line 0's sentence unchanged, no question left.
  - `Picking_over_her_fine_print_asks_and_keeping_hers_fills_only_the_promotion`: fine print "Mine."; pick, `promotion-replace`: `fine-print-question` contains "Replace the fine print too, or keep yours?"; `fine-print-keep`: the promotion is the saved one, the fine print still "Mine.".
  - `With_no_fine_print_block_the_page_says_so`: template without a Fine print block; pick: `promotion-use-message` is "This campaign has no Fine print block, so the promotion's fine print wasn't added."

  In `WorkingCopyPageTests`, beside `A_save_tells_the_other_tab`: `Picking_a_saved_promotion_tells_the_other_tab`: seed "Pick-tells treatment" at 500m and "Pick-tells promo" at 100m through `app.KnownItems` for `DemoApp.Client`; save `CampaignEditor.Start(KnownPromotionTests.Editable).Draft`; `app.Clock.Now += TimeSpan.FromSeconds(1)`; `await TellsTheOtherTab(save.Id, a => a.TypeIntoAsync("promotion-use-saved-1", "Pick-tells promo"))`.
- [ ] **Step 2: Run to see them fail.** Filter `FullyQualifiedName~KnownPromotionTests|FullyQualifiedName~KnownPromotionPageTests|FullyQualifiedName~WorkingCopyPageTests.Picking_a_saved_promotion_tells_the_other_tab`. Expected: build errors naming `UsePromotion`, `AsksBeforeUsing`, `TreatmentId`.
- [ ] **Step 3: Implement the Interfaces.**
- [ ] **Step 4: Run to see them pass.** Same filter, then `FullyQualifiedName~PromotionTests|FullyQualifiedName~SuggestedFixTests|FullyQualifiedName~FieldsTellThePageTests|FullyQualifiedName~TemplateDefaultPageTests` (`PromotionTests.Picking_a_known_treatment_fills_in_its_regular_price_unless_one_is_typed` calls `SetTreatment` directly). Expected: `succeeded: true`.
- [ ] **Step 5: Mutation-check.** Link ignoring case; leave `Rename` linked; fill a missing regular price with "0"; write into a fixed Fine print block; skip the first question; use a promotion that has problems. Each fails its test; restore.
- [ ] **Step 6: Run the whole suite.** Expected: exit 0.
- [ ] **Step 7: Commit:** "A campaign's promotion can be filled from a saved one, asking before it replaces what she wrote"

---

### Task 7: Campaign: "Save as a known promotion", its fine print without the dates

**Files:** Modify `src\Neelam.Campaigns\EditorExport.cs` beside `DatesText`; `src\Neelam.Campaigns\CampaignEditor.cs:15-133`, `:563-658`; `src\Neelam.Campaigns.Storage\KnownItemsSession.cs` after `:278`; `src\Neelam.Web\Components\Pages\CampaignEdit.razor:300-301`, `:790-826`. Test: `KnownPromotionTests`, `KnownPromotionPageTests`.

**Interfaces:**
- Consumes: Tasks 1, 3, 6 (`TreatmentId`, `UsePromotion`); `KnownItemsSession.AddFromPromotionAsync`, `SaveFromCampaignAsync`, `CampaignSaveFailed`.
- Produces:
  - `public static string EditorExport.WithoutLeadingDates(string finePrint, DateOnly starts, DateOnly ends, string? audience)` (below).
  - `public bool PromotionEditor.IsOneOf(KnownItems known)` (as "Decisions" defines it).
  - `public sealed record KnownPromotionFromCampaign(KnownPromotion? Promotion, IReadOnlyList<KnownTreatment> Added, string? Problem);` and `public KnownPromotionFromCampaign CampaignEditor.PromotionToKnown(PromotionBlockEditor block, KnownItems known)`: "Fill in the promotion's name first." (proposed); "Fill in or remove its unfinished lines first." (proposed); a near miss, "Line {n}: Did you mean '{near}'? It's in your known items."; each finished treatment line by its `TreatmentId` when on her list, else `known.TreatmentNamed`, else a new `KnownTreatment(NewId, name, regular)` into `Added`; fine print from the first editable Fine print block, through `WithoutLeadingDates` with the block's dates and audience when both dates are there, else as is; null when blank.
  - `public static Task<IReadOnlyList<string>> KnownItemsSession.SavePromotionFromCampaignAsync(IKnownItemStore store, ActivityTrail trail, KnownPromotion promotion, IReadOnlyList<KnownTreatment> added, ILogger? log = null, CancellationToken ct = default)`: `AddFromPromotionAsync(added)`; then reads her list and points each line at a new treatment to the one of that name now on it (`TreatmentNamed`; another tab may have added it under its own id); then `SaveFromCampaignAsync(promotion)`, whose refusal of a name her saved promotions have is `KnownItemRules`' "already in your known items" (decision 12). Returns the sentences of both, in order. Never throws.
  - Page: under `<PromotionFields>`, when `!promotionBlock.Promotion.IsOneOf(Known)`, `<button class="button" data-testid="promotion-save-known-@index">Save as a known promotion</button>`, disabled with `title` = the problem while `PromotionToKnown` has one; its sentences in `<p role="status" data-testid="promotion-save-note-@index">`; the business re-read afterwards as `SaveKnownAsync` does.

`WithoutLeadingDates` is the one algorithm the tests do not fully fix:

```csharp
public static string WithoutLeadingDates(string finePrint, DateOnly starts, DateOnly ends, string? audience)
{
    foreach (var sentence in new[] { DatesText(starts, ends, audience), DatesText(starts, ends, null) }.Distinct())
    {
        // The sentence as typed by hand: any case, any run of spaces, a hyphen, en or em dash for the en dash.
        var pattern = "^\\s*" + string.Join("\\s+", sentence.Split(' ').Select(w => Regex.Escape(w).Replace("–", "[-–—]")))
                      .Replace("\\s+[-–—]\\s+", "\\s*[-–—]\\s*") + "(?=\\s|$)";
        var match = Regex.Match(finePrint, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success) return finePrint[match.Length..].Trim();
    }
    return finePrint.Trim();
}
```

- [ ] **Step 1: Write the failing tests.** In `KnownPromotionTests`:

```csharp
[Theory]
[InlineData("Offer valid November 1 – December 31, 2026. Not valid with other offers.", "Not valid with other offers.")]
[InlineData("  offer valid November 1 - December 31, 2026.   Not valid with other offers.", "Not valid with other offers.")]
[InlineData("Offer valid November 1 — December 31, 2026, for new clients. Not valid.", "Not valid.")]
[InlineData("Offer valid November 1 – December 31, 2026.", "")]
[InlineData("Offer valid November 2 – December 31, 2026. Not valid.", "Offer valid November 2 – December 31, 2026. Not valid.")]
[InlineData("Not valid. Offer valid November 1 – December 31, 2026.", "Not valid. Offer valid November 1 – December 31, 2026.")]
[InlineData("Offer valid November 1 – December 31, 2026.5 sessions", "Offer valid November 1 – December 31, 2026.5 sessions")]
public void A_leading_validity_sentence_for_the_promotion_s_dates_is_left_out_and_nothing_else(string typed, string kept) =>
    Assert.Equal(kept, EditorExport.WithoutLeadingDates(typed, new(2026, 11, 1), new(2026, 12, 31), "new clients"));
```

  - `Save_as_a_known_promotion_is_offered_only_when_the_promotion_is_not_one_of_hers`: fresh `Editable` editor: `IsOneOf(Known)` false and `PromotionToKnown(...).Problem` "Fill in the promotion's name first."; after `UsePromotion(..., replaceFinePrint: true)` true; `Starts.Text = "2026-11-01"` still true; line 0's price "1499" false.
  - `Saving_from_a_campaign_adds_new_treatments_first_then_links_its_lines_and_drops_the_dates_sentence`: store with `Midface`; `PromotionTests.FillerPromo(new KnownItems([Midface]), Editable)` with fine print "Offer valid November 1 – December 31, 2026. Not valid with other offers."; `SavePromotionFromCampaignAsync(store, TestRecords.Unwatched.For(Salon, Actor.Demo), made.Promotion!, made.Added)` says `["Added Full-Face Facial Balancing to your treatments, regularly $5,100.", "Saved \"Filler Promo\" to your known items."]`; the saved lines point at `Midface.Id` and the new Balancing's id; its `FinePrint` is "Not valid with other offers.".
  - `Saving_a_promotion_with_a_near_miss_line_is_refused_with_did_you_mean`: line 1 set to "Full-Face Facial Balancin": `Problem` is "Line 2: Did you mean 'Full-Face Facial Balancing'? It's in your known items."
  - `Saving_a_promotion_named_as_one_of_hers_says_it_is_already_known_and_a_store_failure_is_said_not_thrown` (decision 12): store with `Midface, Balancing, Holiday()`; saving `Holiday() with { Id = KnownItem.NewId() }` says `["\"Holiday filler promo\" is already in your known items."]`; over a `FailingStore` (private `IKnownItemStore` reading `KnownItems.None`, throwing `Azure.RequestFailedException(503, ...)` on add, as `PromotionTests.Unreachable`) it says `[KnownItemsSession.CampaignSaveFailed]` and the single `ListLogger<KnownItemsSession>` entry does not contain "Holiday".

  In `KnownPromotionPageTests`: `Save_as_a_known_promotion_saves_it_says_so_and_is_then_no_longer_offered`: a saved `FillerPromo(template: Editable)` with a suffixed name; `ClickAsync("promotion-save-known-1")`: `promotion-save-note-1` contains `Saved "<name>" to your known items.`, `RenderedPage.Count(html, "promotion-save-known-1") == 0`, `app.KnownItems` holds it.
- [ ] **Step 2: Run to see them fail.** Filter `FullyQualifiedName~KnownPromotionTests|FullyQualifiedName~KnownPromotionPageTests`. Expected: build errors naming `WithoutLeadingDates`, `IsOneOf`, `PromotionToKnown`, `SavePromotionFromCampaignAsync`.
- [ ] **Step 3: Implement the Interfaces.**
- [ ] **Step 4: Run to see them pass.** Same filter, then `FullyQualifiedName~PromotionPageTests|FullyQualifiedName~KnownItemsTests`. Expected: `succeeded: true`.
- [ ] **Step 5: Mutation-check.** Drop the `(?=\s|$)` look-ahead (the "2026.5 sessions" row fails); stop mapping hyphens (row 2 fails); compare dates in `IsOneOf`; skip the relink after adding; keep the dates sentence; treat a near miss as new. Restore each.
- [ ] **Step 6: Run the whole suite.** Expected: exit 0.
- [ ] **Step 7: Commit:** "A campaign's own promotion can be saved as a known one, its new treatments joining her list first"

---

### Task 8: Campaign: following her list on opening until approved

**Files:** Modify `src\Neelam.Campaigns\CampaignEditor.cs:15-133`, `:563-658`, `:676-712`; `src\Neelam.Campaigns.Storage\DraftSession.cs:164-169`; `src\Neelam.Web\WorkingCopyKeeper.cs:83-94`; `src\Neelam.Web\Components\Pages\CampaignEdit.razor:992-1021` and `:190-200`. Test: `KnownPromotionTests`, `KnownPromotionPageTests`, `WorkingCopyPageTests.cs:483-499`, `tests\Neelam.Campaigns.Tests\ApprovalsRequiredTests.cs`.

**Interfaces:**
- Consumes: Task 6's `TreatmentId` and `UsePromotion`; `DraftSession.Latest`, `SaveRef.Approval` (null while approved only in part: `SaveRef.PartApproval`).
- Produces:
  - `public string? PricedTreatmentEditor.Follow(KnownTreatment treatment)`: sets the name (ordinal difference) and, when hers has one that differs, the regular price; returns the sentence or null. Price only: "From your treatments: {name} is now regularly {new} (was {old})." (old missing: "From your treatments: {name} is regularly {new}." proposed); name only: "From your treatments: {old} is now called {new}." (proposed); both: "From your treatments: {old} is now called {new}, regularly {price} (was {oldPrice})." (proposed). The link stays.
  - `public IReadOnlyList<string> CampaignEditor.FollowKnownTreatments(KnownItems known)`: every linked line of every promotion block whose treatment is on her list; sentences in order, duplicates once. A treatment not on her list, or without a regular price, leaves the line's price as it is.
  - `public IReadOnlyList<string> DraftSession.FollowHerList(KnownItems known)`: `[]` when `Latest?.Approval is not null`; else `Editor.FollowKnownTreatments(known)`. A part approval leaves `Approval` null, so it follows (decision 9).
  - `public void WorkingCopyKeeper.TakeAsSeen()`: `_seen = Form()`. Doc: "the form as it is now is what the page last saw: for a change the page makes by itself (following her list on opening), which is neither written as a working copy nor told to her tab."
  - `CampaignEdit.OnInitializedAsync`: after `LoadChecksAsync` succeeds, `_followed = _session?.FollowHerList(Known) ?? []; _keeper.TakeAsSeen();` (the keeper has already tracked the form before `LoadChecksAsync`, so the first `LookedAsync` sees no change). Shown as `<section class="card" role="status" data-testid="known-followed"><p>@string.Join(" ", _followed)</p></section>` while not empty; cleared by Save, Load the latest and Discard.

- [ ] **Step 1: Write the failing tests.** In `KnownPromotionTests` (each first uses `Known.Promotions[0]` on a `CampaignEditor.Start(Editable)` with `replaceFinePrint: false`):
  - `Following_her_list_takes_each_linked_line_s_current_name_and_price_and_says_what_changed`: over `[Midface with { Name = "Midface Lift" }, Balancing with { RegularPrice = 5400m }, Holiday()]`: `FollowKnownTreatments` is `["From your treatments: Midface Lift / Cheek Rejuvenation is now called Midface Lift.", "From your treatments: Full-Face Facial Balancing is now regularly $5,400 (was $5,100)."]`; line 0's name "Midface Lift", line 1's regular "5400"; a second call is empty.
  - `Lines_typed_by_hand_are_never_followed`: `PromotionTests.FillerPromo(template: Editable).FollowKnownTreatments(new KnownItems([Balancing with { RegularPrice = 9000m }]))` is empty.
  - `A_line_whose_treatment_was_removed_or_lost_its_price_is_left_as_it_is` (Review Focus 5): over `[Balancing with { RegularPrice = null }]`: empty; lines are ("Midface Lift / Cheek Rejuvenation", "2550") and regular "5100".

  In `ApprovalsRequiredTests`, beside `With_two_required_a_version_is_approved_once_two_different_people_have`: `A_version_approved_in_part_follows_her_list_on_opening` (decision 9): `PromotionTests.FillerPromo(template: KnownPromotionTests.Editable)` using `KnownPromotionTests.Known.Promotions[0]` with `replaceFinePrint: true`; saved through `this.Store(2)`, clock on a minute, opened and `ApproveAsync("Priya")`; reopened over `this.Store(2)`: `CurrentPartApproval` is not null and `FollowHerList(new KnownItems([KnownPromotionTests.Midface, KnownPromotionTests.Balancing with { RegularPrice = 5400m }]))` is `["From your treatments: Full-Face Facial Balancing is now regularly $5,400 (was $5,100)."]`.

  In `KnownPromotionPageTests` (seed a suffixed treatment at $5,100, save a campaign whose line is linked to it, then `UpdateAsync` it to $5,400):
  - `An_unapproved_campaign_follows_her_list_on_opening_with_the_note_and_saves_nothing`: `known-followed` is "From your treatments: <name> is now regularly $5,400 (was $5,100)."; `line-sentence-1-l0` reads "(Regularly $5,400, ..."; `Store.HistoryAsync(DocumentKind.Draft, id)` still single; `save-button` saves it at $5,400 and the note goes.
  - `An_approved_campaign_is_not_changed_and_the_worth_a_look_stands`: `store.ApproveAsync(save, "Priya")` before moving the price: no `known-followed`; `line-sentence-1-l0` still "$5,100"; `checks-findings` contains "is regularly $5,400 in your known items; here it is $5,100." and `approved` is shown.

  In `WorkingCopyPageTests`, beside `Opening_the_campaign_and_bringing_back_its_working_copy_tell_no_one`: `Following_her_list_on_opening_tells_no_one_and_writes_no_working_copy`: "Follow-quiet treatment" at 5100m; an `Editable` campaign with a line `SetTreatment(t.Name, ...)` at "100", saved; tab `a` opened on `Clock()`; the treatment updated to 5400m; tab `b` opened on its own clock, `Later()`, `clockB.Advance(Settings.Cap)`, `Settled(b)`: `known-followed` once in `b`, `AnyTyped(save.Id)` false, no `working-copy-other-tab` in `a`, `Held(save.Id)` null.
- [ ] **Step 2: Run to see them fail.** Filter `FullyQualifiedName~KnownPromotionTests|FullyQualifiedName~KnownPromotionPageTests|FullyQualifiedName~WorkingCopyPageTests|FullyQualifiedName~ApprovalsRequiredTests`. Expected: build errors naming `FollowKnownTreatments`, `FollowHerList`, `TakeAsSeen`.
- [ ] **Step 3: Implement the Interfaces.**
- [ ] **Step 4: Run to see them pass.** Same filter, then `FullyQualifiedName~EditingTabNoticeTests|FullyQualifiedName~ApprovalTests|FullyQualifiedName~PromotionPageTests`. Expected: `succeeded: true`.
- [ ] **Step 5: Mutation-check.** Drop `TakeAsSeen`; follow an approved version; also skip a part-approved one (`PartApproval is not null`, the decision-9 test fails); follow unlinked lines by name; empty a removed treatment's price. Each fails its test; restore.
- [ ] **Step 6: Run the whole suite** twice in a row (`WorkingCopyPageTests` runs on clocks; a test passing once and failing once is a flaky test to fix now with its root cause, never a rerun). Expected: exit 0 both times.
- [ ] **Step 7: Commit:** "An unapproved campaign takes her treatments' current names and prices when opened, without counting as editing"

---

### Task 9: WIP.md: what was built and what awaits the owner and Priya

**Files:** Modify `C:\repos\neelam-aesthetics\WIP.md`: a new bullet **"Known promotions (owner, 2026-11-06; built ..., not deployed)"** after the "Promotions (answer 9a and P1-P6)" bullet in "Pri-1 from Priya" (ends at `:880`), and one line in "Known items" (`:434-445`) adding `Promotion` to the table's kinds and its columns (`Lines`, `Summary`, `Audience`, `FinePrint`).

- [ ] **Step 1: Write the bullet**, in the file's own style: the owner's words ("What we want is automatic consistency"; spec decisions 1-12); what was built, by class and file (Tasks 1-8); the tests by class (`KnownPromotionTests`, `KnownPromotionPageTests`, and those added to `PromotionTests`, `KnownItemPagesTests`, `KnownItemsTabPageTests`, `WorkingCopyPageTests`, `ApprovalsRequiredTests`), each mutation-checked; **"Draft words, awaiting the owner / Priya"**: every sentence marked (proposed) in Tasks 1-8, verbatim, beside the spec's own; **"Decisions made while building, the owner's to overrule"**: the list under "Decisions this plan makes"; **"Not done"**: updating a saved promotion from inside a campaign (spec, out of scope), following on Load the latest / Undo / Restore, a browser run; **"After deploy"**: the spec's staging browser run on a BROWSER TEST campaign, which changes staging's copy of her known items, so ask first; entering her two known prices on her site is Priya's data, entered by her or with her say-so. Rollback: an older build leaves a Promotion row out and logs it by its key (`KnownItemTable.ReadAll`, already so), ignores `treatmentId`, and lets a used treatment be removed.
- [ ] **Step 2: Check every class, method, test and file name in the new text against the code** with RazorGraph (`mcp__razorgraph__find_nodes`, graph `neelam`, rebuilt with `build_solution` after Task 8's commit).
- [ ] **Step 3: Commit:** "WIP.md records known promotions as built, and the words and calls awaiting the owner and Priya"
