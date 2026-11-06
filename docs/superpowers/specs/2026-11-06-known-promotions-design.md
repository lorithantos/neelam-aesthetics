# Known promotions -- design

Date: 2026-11-06. Status: approved in conversation by the owner, section by section; this
document awaits the owner's review. Words shown to the client are drafts for Priya.

## Why

Priya's Holiday Filler Promo (approved 31 Oct 2026, saved before the Promotion block existed)
typed its offer by hand into a Paragraphs block:

> Midface Lift / Cheek Rejuvenation — $1,599 (Regularly $2,550, saving $951)
> Full-Face Facial Balancing — $3,499 (Regularly $5,100, saving $1601)

The numbers are right, but nothing checked them: prose is never compared with known items
(owner, 2026-10-09), the promotion-price rules run only on the Promotion block, and "$1601" lost
its comma. The treatment was also written three ways in one email. There will likely be more
promotions than tiered offers (owner, 2026-11-06), so a promotion she runs again should be picked,
not retyped.

## The owner's rule: automatic consistency

"What we want is automatic consistency" (owner, 2026-11-06). Each fact has one source; everything
else is derived from it:

| Fact | Its one source | Derived, never typed |
|---|---|---|
| Treatment name | her treatments (known items) | saved promotion lines; linked campaign lines |
| Regular price | her treatments | the line's regular price |
| Saving | regular price minus promotion price | "saving $1,601" |
| Promotion dates | the campaign's Promotion block | the "Offer valid …" sentence that block ends with |

## Owner decisions (2026-11-06)

1. The standard price list is her known treatments' regular prices (`KnownTreatment.RegularPrice`),
   not a list of its own.
2. A saved promotion keeps its lines and its fine print; it takes new dates each time it is used.
3. A saved promotion carries its own fine print, filling the campaign's Fine print block.
4. When a saved promotion is used, regular prices come fresh from her treatments.
5. Approach A: a fourth kind of known item, a promotion, with its lines pointing at her treatments.
6. A campaign whose newest version is not approved follows her list when opened; an approved
   version never changes.
7. A product new to her list is added to it wherever it first appears.

The owner's answers on the plan (2026-11-06, "Yes, take them"):

8. No fine-print dates flag. The Promotion block's export already ends "Offer valid …"
   (`EditorExport.DatesText`), so the dates are said once, by the block that owns them.
9. A newest version with some but not all of its approvals is not approved, and follows her list.
10. Lowering a regular price to or below a saved promotion's price is allowed: her list is the one
    source. That saved promotion shows the problem on its card and cannot be used until fixed.
11. A Fine print block the template fixes is left alone when a saved promotion is picked.
12. Saving a promotion under a name her saved promotions already have is refused, as tiers are.

Not decided here, and not changed by this design: whether known items supersede `ClientCatalog`
(WIP open item 8), and whether Remove on a known item asks first or is restorable (thread item
"Known item Remove deletes in one click"). Removing a saved promotion behaves as removing a tier
does, and follows whatever is decided there.

## 1. What is kept and where

### The model

`KnownItemKind.Promotion`, and a new record beside `KnownTier` in `src\Neelam.Campaigns\KnownItems.cs`:

```
KnownPromotion(Id, Name, Summary?, Audience?, Lines, FinePrint?) : KnownItem
  Kind => Promotion; Text => Name

KnownPromotionLine (JSON-polymorphic, discriminator "line")
  KnownTreatmentLine(TreatmentId, Price)   "treatment"
  KnownBenefitLine(Benefit)                 "benefit"
```

- A treatment line keeps the treatment's id and the promotion price only. No name and no regular
  price are stored: both are read from her treatments whenever the promotion is shown or used, and
  the saving is always computed (`PricedTreatment.Saving`).
- A benefit line copies the benefit, as `KnownTier` copies its benefits: a benefit's amounts belong
  to the promotion.
- `FinePrint` is the saved fine print without its validity sentence (section 4).
- No dates.

### Storage

One more kind in the existing `knownItems` table (`src\Neelam.Campaigns.Storage\KnownItemTable.cs`):
partition = client, row key = item id, `Kind` = "Promotion", `Text` = name, plus columns `Summary`,
`Audience`, `Lines` (JSON, compact, as `Benefits` is for a tier) and `FinePrint`. No new table, so
no infrastructure deploy.

### Consistency on her list

- Renaming a treatment or changing its regular price flows into every saved promotion using it.
- Removing a treatment a saved promotion uses is refused, naming it. Draft words: "Used by Holiday
  filler promo. Change or remove that promotion first."
- Lowering a treatment's regular price to or below a saved promotion's price for it goes through
  (decision 10). The saved promotion's card then shows the problem, from `KnownItemRules.Problems`,
  and it cannot be used until she fixes it.
- A line whose treatment id is not on her list (removed in another tab at the same moment) shows
  "a treatment no longer on your list" and must be fixed before the promotion can be used. It is
  never dropped silently.

### New products join her list wherever they first appear

- Already built (owner, 2026-11-01, P6): saving a campaign adds its promotion's new treatments with
  their regular prices (`KnownItemsSession.AddFromPromotionAsync`).
- Saving a promotion from a campaign adds its new treatments first, then links the saved lines to
  them.
- Building a promotion on the Known items page with a treatment not on her list adds it, with the
  regular price if given.
- She is told each time, in today's words: "Added Full-Face Facial Balancing to your treatments,
  regularly $5,100."
- Not new: a near miss (`KnownItemMatch.NearMiss`) draws "Did you mean" and is not added; a name
  her list already has, ignoring case and plural endings, is that treatment. A new product with no
  regular price is added without one.
- Products are not read out of prose.

### Campaign model change

`PricedTreatment` gains optional `TreatmentId`: the known treatment the line came from. A line
picked from a saved promotion or from her list carries it; a line typed by hand whose name matches
one of her treatments exactly is linked the same way. Older saves have none and read as before.

## 2. The Known items page

Tabs, in order: Treatments, **Promotions**, Benefit lines, Tiers, Tier name ladders
(`KnownItemsTab`, `KnownItemsPage.razor`).

### Treatments tab

- Each treatment shows its regular price ("Full-Face Facial Balancing, regularly $5,100") or
  "no regular price yet".
- Each shows where saved promotions use it: "Used in: Holiday filler promo".
- Changing a name or price says how far it reaches: "Used in 2 saved promotions; they will show
  the new price."

### Promotions tab

- Build a promotion: name, summary (optional), audience (optional), lines, fine print.
  - A treatment line picks one of her treatments or takes a new name (which joins her list). She
    types the promotion price. "The email says:" shows the line exactly as it goes out.
  - The regular price beside it is her list's. Editing it edits her list (one fact), and the box
    says so: "Changing it here changes it on your treatments and in every promotion that uses it."
    For a treatment with no price yet, this is where she gives one.
  - A benefit line uses the tier's benefit editor.
  - Fine print: a text box. It holds no dates: the campaign's Promotion block states them.
- Saved promotions as cards: name, each line as the email says it at today's prices, the fine
  print; Change and Remove, following the tier's pattern.
- Campaigns that used a removed promotion keep their text, as with tiers.

## 3. In the campaign

### Using a saved promotion

- "Use a saved promotion" is the first thing in the Promotion block (`PromotionFields.razor`), a
  `KnownItemPicker` over her saved promotions.
- Picking fills the name, summary, audience and lines (treatment lines linked by id; names and
  regular prices from her list today; promotion prices from the saved promotion). Dates are left
  empty, and the block's Still to do says so.
- It fills the Fine print block with the saved fine print, as ordinary text. The dates come from
  the Promotion block (section 4).
- A Fine print block the template fixes is left alone (decision 11): "This campaign's fine print
  is the template's own, so the promotion's fine print wasn't added."
- She is asked before anything she wrote is replaced: "Replace this promotion with Holiday filler
  promo?" when the block has lines; "Replace the fine print too, or keep yours?" when the Fine
  print block has text.
- A campaign with no Fine print block: "This campaign has no Fine print block, so the promotion's
  fine print wasn't added." (Closes when campaigns can add blocks, Pri-1 "Template should not
  restrict the campaign".)
- Everything filled is ordinary fields; editing them never changes the saved promotion.
- Picking is her action, so it counts as editing (owner's every-action rule, 2026-11-05).

### Saving a promotion from a campaign

- When the campaign's promotion is not one of her saved ones (written fresh, or picked and
  changed), "Save as a known promotion" appears beside it, as "Save as a known item" does for a
  tier. A button, not automatic: one-off promotions should not pile up.
- A name her saved promotions already have is refused as already in her known items, as a tier's
  is (decision 12).
- Not built now: updating a saved promotion from inside a campaign.

### Following her list until approved

- A newest version with some but not all of its approvals is not approved (decision 9).
- On opening a campaign whose newest version is not approved, each linked promotion line takes its
  treatment's current name and regular price from her list. A note says what changed. Draft words:
  "From your treatments: Full-Face Facial Balancing is now regularly $5,400 (was $5,100)."
- The change is unsaved until she saves, like any edit.
- It is the page acting, not her: it does not count as editing (no other-tab notice), and it is not
  written as a working copy, since it is redone on every open until saved.
- An approved newest version is never changed. If her list has moved, the existing Worth a look
  stands ("'X' is regularly $5,400 in your known items; here it is $5,100").

## 4. Fine print and dates, checks, rollback, testing

### The dates are said once

- The Promotion block's export already ends with its validity sentence, built from its dates each
  time (`EditorExport.DatesText`): "Offer valid November 1 – December 31, 2026." Nothing is added
  to the Fine print block for dates, and the block is unchanged (decision 8).
- Saved fine print carries no dates. Saving a promotion from a campaign removes a leading validity
  sentence that matches the promotion's dates from the saved fine print.
- No new rule reads dates out of prose; the LLM proofread already reads the fine print.

### Checks

- Existing campaign checks stay as they are: `PromotionPrices`, `PromotionDates`, and the promotion
  loop in `KnownItemChecks`.
- `KnownItemRules.Problems` for a saved promotion: a name, unique among her saved promotions; at
  least one line; each treatment line's treatment on her list; promotion price above $0 and, where
  her list has a regular price, below it.
- Removing a used treatment is refused (section 1).

### Rollback (only if staging is rolled back to Build 209)

- An older build skips a "Promotion" row in `knownItems` and logs it; her other known items load.
- The new optional campaign field (`TreatmentId`) is ignored by an older build: campaign JSON does
  not refuse unknown members.

### Testing

No test touches the network. In-memory fakes throughout.

- Model and JSON: a saved promotion round-trips; older campaigns and known items read unchanged.
- Store: the Promotion kind round-trips through `InMemoryKnownItemStore` and the table mapping.
- Consistency: a rename or price change on her list shows in every saved promotion; removing a used
  treatment is refused with the promotion's name; a line to a missing treatment blocks use.
- New products: added from a campaign save, from saving a promotion, and from the Known items page;
  a near miss is not added.
- Campaign circuit tests (`OpenCampaignPage`): picking fills the block, lines and fine print with
  dates empty; asking before replacing; an unapproved campaign follows her list on open with the
  note, no other-tab notice and no working copy; an approved one does not change; Save as a known
  promotion is offered only when it should be.
- Fine print: a picked promotion's dates appear once in the export, from the Promotion block;
  saving a promotion strips a matching leading validity sentence and keeps one that doesn't match;
  a template-fixed Fine print block is left alone.
- Her list: lowering a regular price below a saved promotion's price goes through and that
  promotion shows the problem; a partly approved campaign follows her list on open.
- Every new test is mutation-checked.
- After deploy: a browser run on staging on a BROWSER TEST campaign. Building a test promotion
  there changes staging's copy of her known items, so ask first.

## Out of scope

- Reading products or prices out of prose.
- Updating a saved promotion from inside a campaign.
- Benefit lines that point at her saved benefit lines.
- The `ClientCatalog` decision (WIP item 8).
- Entering the two known prices on her site: that is Priya's data, entered by her (or with her
  say-so) once this ships.
