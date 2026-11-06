using System.Net;
using System.Text.RegularExpressions;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// A saved promotion (known items' fourth kind): its lines point at her treatments for the name and
/// the regular price, so the saving is worked out and a change of price reaches every promotion.
/// </summary>
public class KnownPromotionTests
{
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

    #region Model

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

    [Fact]
    public void Her_list_answers_which_saved_promotions_use_a_treatment_and_names_a_treatment_as_the_checks_do()
    {
        var holiday = Known.Promotions.Single();
        Assert.Equal([holiday.Id], Known.PromotionsUsing(Balancing.Id).Select(p => p.Id));
        Assert.Empty(Known.PromotionsUsing(Wellness.Id));
        Assert.True(holiday.Uses(Midface.Id));
        Assert.False(holiday.Uses(Wellness.Id));
        Assert.Same(Balancing, Known.TreatmentNamed("full-face facial balancings"));
        Assert.Null(Known.TreatmentNamed("Full-Face Facial Balancin"));
        Assert.Same(Balancing, Known.TreatmentById(Balancing.Id));
        Assert.Null(Known.TreatmentById(KnownItem.NewId()));
        Assert.Same(holiday, Known.PromotionNamed("holiday filler promo"));
        Assert.Equal(KnownItemKind.Promotion, holiday.Kind);
        Assert.Equal("Holiday filler promo", holiday.Text);
        Assert.False(holiday.HasMissingTreatment(Known));
        Assert.True(Holiday(new KnownTreatmentLine(KnownItem.NewId(), 10m)).HasMissingTreatment(Known));
    }

    [Fact]
    public void A_promotion_s_lines_round_trip_through_compact_json_with_their_kind()
    {
        KnownPromotionLine[] lines = [new KnownTreatmentLine(Balancing.Id, 149.50m), new KnownBenefitLine(new Credit(50m, "your next appointment"))];
        var json = CampaignJson.SerializePromotionLines(lines);
        Assert.StartsWith($$"""[{"line":"treatment","treatmentId":"{{Balancing.Id}}","price":149.50},{"line":"benefit",""", json);
        Assert.DoesNotContain('\n', json);
        Assert.Equal(lines, CampaignJson.DeserializePromotionLines(json));
        Assert.Throws<InvalidDataException>(() => CampaignJson.DeserializePromotionLines("null"));
    }

    #endregion

    #region Store

    private static readonly Actor Priya = new("Priya Sharma", FromSignIn: true);

    [Fact]
    public void A_promotion_round_trips_through_its_table_row()
    {
        var promotion = Holiday() with { Summary = "Two months to refresh.", Audience = "new clients" };
        var row = KnownItemTable.FromItem(Salon, promotion);
        Assert.Equal("Promotion", row.GetString("Kind"));
        Assert.Equal("Holiday filler promo", row.GetString("Text"));
        Assert.Equal(CampaignJson.SerializePromotionLines(promotion.Lines), row.GetString("Lines"));
        Assert.Equal("Two months to refresh.", row.GetString("Summary"));
        Assert.Equal("new clients", row.GetString("Audience"));
        Assert.Equal(promotion.FinePrint, row.GetString("FinePrint"));

        var back = Assert.IsType<KnownPromotion>(KnownItemTable.ToItem(Salon, row));
        Assert.Equal(promotion.Name, back.Name);
        Assert.Equal(promotion.Summary, back.Summary);
        Assert.Equal(promotion.Audience, back.Audience);
        Assert.Equal(promotion.FinePrint, back.FinePrint);
        Assert.Equal(promotion.Lines, back.Lines);
    }

    [Fact]
    public void A_promotion_with_no_summary_audience_or_fine_print_has_no_such_columns()
    {
        var row = KnownItemTable.FromItem(Salon, Holiday() with { FinePrint = null });
        Assert.False(row.ContainsKey("Summary"));
        Assert.False(row.ContainsKey("Audience"));
        Assert.False(row.ContainsKey("FinePrint"));
    }

    [Fact]
    public void A_promotion_row_that_cannot_be_read_is_left_out_and_logged_by_its_key_alone()
    {
        var log = new ListLogger<KnownItemTable>();
        string?[] bad = [null, "{not json", "[null]", """[{"line":"treatment","treatmentId":"","price":1}]""", """[{"line":"benefit"}]"""];
        var rows = bad.Select(lines =>
        {
            var row = new Azure.Data.Tables.TableEntity(Salon.Value, KnownItem.NewId()) { ["Kind"] = "Promotion", ["Text"] = "Secret" };
            if (lines is not null) row["Lines"] = lines;
            return row;
        }).Append(KnownItemTable.FromItem(Salon, Balancing)).ToList();

        var read = KnownItemTable.ReadAll(Salon, rows, log);

        Assert.Equal([Balancing.Id], read.All.Select(i => i.Id));
        Assert.Equal(5, log.Entries.Count);
        Assert.DoesNotContain(log.Entries, e => e.ToString()!.Contains("Secret"));
    }

    [Fact]
    public async Task A_promotion_is_kept_by_the_store_only_with_its_treatments_on_her_list()
    {
        var store = new InMemoryKnownItems();
        var holiday = Holiday();
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(Salon, holiday));
        Assert.Contains("a treatment no longer on your list", refused.Message);

        await store.AddAsync(Salon, Midface);
        await store.AddAsync(Salon, Balancing);
        await store.AddAsync(Salon, holiday);
        Assert.Equal(holiday.Lines, (await store.ForClientAsync(Salon)).Promotions.Single().Lines);
    }

    [Fact]
    public async Task Removing_a_treatment_a_saved_promotion_uses_is_refused_naming_it()
    {
        var store = new InMemoryKnownItems();
        foreach (var item in new KnownItem[] { Midface, Balancing, Wellness })
            await store.AddAsync(Salon, item);
        await store.AddAsync(Salon, Holiday());
        var records = new TestRecords(new ManualClock(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)));
        var session = await KnownItemsSession.OpenAsync(store, records.Recorder.For(Salon, Priya));

        await session.RemoveAsync(Balancing);

        Assert.Equal(["Used by Holiday filler promo. Change or remove that promotion first."], session.Errors);
        Assert.NotNull((await store.ForClientAsync(Salon)).TreatmentById(Balancing.Id));
        Assert.Empty(records.Activity.Events);

        await session.RemoveAsync(Wellness);

        Assert.Equal("Removed \"Wellness injection\". Campaigns that used it keep their text.", session.Message);
        Assert.Null((await store.ForClientAsync(Salon)).TreatmentById(Wellness.Id));
    }

    [Fact]
    public void The_store_rules_name_every_promotion_that_uses_a_treatment()
    {
        var known = new KnownItems([Midface, Balancing, Holiday(), Holiday() with { Id = KnownItem.NewId(), Name = "Spring refresh" }]);
        var ex = Assert.Throws<InvalidOperationException>(() => KnownItemStoreRules.CheckRemove(known, Balancing.Id));
        Assert.Equal("Used by Holiday filler promo and Spring refresh. Change or remove those promotions first.", ex.Message);   // (proposed)
        KnownItemStoreRules.CheckRemove(known, KnownItem.NewId());
    }

    #endregion

    #region Session

    private static async Task<(KnownItemsSession Session, InMemoryKnownItems Store, TestRecords Records)> Session(params KnownItem[] items)
    {
        var store = new InMemoryKnownItems();
        foreach (var item in items) await store.AddAsync(Salon, item);
        return await Open(store);
    }

    private static async Task<(KnownItemsSession Session, InMemoryKnownItems Store, TestRecords Records)> Open(InMemoryKnownItems store)
    {
        var records = new TestRecords(new ManualClock(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)));
        return (await KnownItemsSession.OpenAsync(store, records.Recorder.For(Salon, Priya)), store, records);
    }

    private static KnownTreatmentLineForm TreatmentLine(KnownItemsSession session, KnownPromotionForm form, string treatment, string price, string? regular = null)
    {
        var line = form.AddTreatment();
        line.SetTreatment(treatment, session.Items);
        line.Price = price;
        if (regular is not null) line.RegularPrice = regular;
        return line;
    }

    [Fact]
    public async Task A_promotion_built_from_her_treatments_is_saved_with_its_lines_by_id()
    {
        var (session, store, _) = await Session(Midface, Balancing);
        session.NewPromotion.Name = "Holiday filler promo";
        var line = TreatmentLine(session, session.NewPromotion, "Full-Face Facial Balancing", "3,499");

        Assert.Equal("5100", line.RegularPrice);
        Assert.Equal("Full-Face Facial Balancing — $3,499 (Regularly $5,100, saving $1,601)", line.Sentence(session.Items));
        Assert.True(await session.AddPromotionAsync());

        Assert.Equal([new KnownTreatmentLine(Balancing.Id, 3499m)], (await store.ForClientAsync(Salon)).Promotions.Single().Lines);
        Assert.Equal("Added \"Holiday filler promo\".", session.Message);
    }

    [Fact]
    public async Task A_new_treatment_named_on_the_promotion_form_joins_her_list_with_its_regular_price()
    {
        var (session, store, records) = await Session(Midface);
        session.NewPromotion.Name = "Holiday filler promo";
        TreatmentLine(session, session.NewPromotion, "Full-Face Facial Balancing", "3499", "5,100");
        TreatmentLine(session, session.NewPromotion, "Lip filler", "499");

        Assert.True(await session.AddPromotionAsync());

        Assert.StartsWith("Added Full-Face Facial Balancing to your treatments, regularly $5,100. Added Lip filler to your treatments.", session.Message);
        var known = await store.ForClientAsync(Salon);
        var balancing = known.TreatmentNamed("Full-Face Facial Balancing")!;
        Assert.Equal(5100m, balancing.RegularPrice);
        Assert.Null(known.TreatmentNamed("Lip filler")!.RegularPrice);
        Assert.Equal(balancing.Id, Assert.IsType<KnownTreatmentLine>(known.Promotions.Single().Lines[0]).TreatmentId);
        Assert.Equal(3, records.Activity.Events.Count(e => e.Action == ActivityAction.KnownItemAdded));
    }

    [Fact]
    public async Task A_name_her_list_has_ignoring_case_and_plural_is_that_treatment_and_a_near_miss_is_refused()
    {
        var (session, store, _) = await Session(Balancing);
        session.NewPromotion.Name = "Holiday filler promo";
        TreatmentLine(session, session.NewPromotion, "full-face facial balancings", "3499", regular: "");   // a blank box changes nothing
        var near = TreatmentLine(session, session.NewPromotion, "Full-Face Facial Balancin", "100");

        Assert.False(await session.AddPromotionAsync());

        Assert.Equal(["Line 2: Did you mean 'Full-Face Facial Balancing'? It's in your known items."], session.Errors);
        Assert.Single((await store.ForClientAsync(Salon)).All);

        session.NewPromotion.RemoveLine(near);
        Assert.True(await session.AddPromotionAsync());
        var known = await store.ForClientAsync(Salon);
        Assert.Equal([new KnownTreatmentLine(Balancing.Id, 3499m)], known.Promotions.Single().Lines);
        Assert.Equal(5100m, known.TreatmentById(Balancing.Id)!.RegularPrice);
    }

    [Fact]
    public async Task Changing_the_regular_price_on_the_promotion_form_changes_her_treatment_everywhere()
    {
        var spring = Holiday() with { Id = KnownItem.NewId(), Name = "Spring refresh" };
        var (session, store, _) = await Session(Midface, Balancing, spring);
        session.NewPromotion.Name = "Holiday filler promo";
        TreatmentLine(session, session.NewPromotion, "Full-Face Facial Balancing", "3499", "5400");

        Assert.True(await session.AddPromotionAsync());

        Assert.Contains("Changed Full-Face Facial Balancing's regular price to $5,400.", session.Message);
        var known = await store.ForClientAsync(Salon);
        Assert.Equal(5400m, known.TreatmentById(Balancing.Id)!.RegularPrice);
        Assert.Equal("Full-Face Facial Balancing — $3,499 (Regularly $5,400, saving $1,901)",
            known.PromotionNamed("Spring refresh")!.Shown(known)[1]);
    }

    // Final review, Important 1: typing "Botox Cosmetic" passes through "Botox", an exact match. A box the
    // form filled follows the treatment named; only a price she typed is a new one for her list.
    [Fact]
    public void A_regular_price_the_form_filled_in_follows_the_treatment_named_and_only_one_she_typed_reprices()
    {
        var botox = new KnownTreatment(KnownItem.NewId(), "Botox", 15m);
        var cosmetic = new KnownTreatment(KnownItem.NewId(), "Botox Cosmetic", 500m);
        var known = new KnownItems([botox, cosmetic]);
        var form = new KnownPromotionForm { Name = "Smooth start" };
        var line = form.AddTreatment();
        line.Price = "400";

        line.SetTreatment("Botox", known);
        Assert.Equal("15", line.RegularPrice);
        line.SetTreatment("Botox Cosmetic", known);
        Assert.Equal("500", line.RegularPrice);
        var (plan, errors) = form.ToPlan(KnownItem.NewId(), known);
        Assert.Empty(errors);
        Assert.Empty(plan!.Repriced);
        Assert.Equal([new KnownTreatmentLine(cosmetic.Id, 400m)], plan.Promotion.Lines);

        // Her list changed meanwhile (another tab): a filled box is not her price, so nothing is put back.
        Assert.Empty(form.ToPlan(KnownItem.NewId(), new KnownItems([botox, cosmetic with { RegularPrice = 550m }])).Plan!.Repriced);

        line.SetTreatment("Lip filler", known);                     // new to her list: the filled price goes
        Assert.Equal("", line.RegularPrice);
        Assert.Null(form.ToPlan(KnownItem.NewId(), known).Plan!.Added.Single().RegularPrice);

        line.SetTreatment("Botox", known);
        line.RegularPrice = "450";                                    // typed by her: hers, whatever is named
        line.SetTreatment("Botox Cosmetic", known);
        Assert.Equal("450", line.RegularPrice);
        Assert.Equal([cosmetic with { RegularPrice = 450m }], form.ToPlan(KnownItem.NewId(), known).Plan!.Repriced);

        // A saved promotion opened to change: its boxes are her treatments' prices, filled, and follow a new name.
        var editing = KnownPromotionForm.Of(Holiday(), Known);
        var balancing = Assert.IsType<KnownTreatmentLineForm>(editing.Lines[1]);
        balancing.SetTreatment(Midface.Name, Known);
        Assert.Equal("2550", balancing.RegularPrice);
    }

    // Final review, Minor 3: lines naming one new treatment keep the first regular price given, as Save as a known promotion does.
    [Fact]
    public void Lines_naming_one_new_treatment_keep_the_first_regular_price_given()
    {
        var form = new KnownPromotionForm { Name = "Lip week" };
        foreach (var (regular, price) in new[] { ("", "199"), ("699", "499"), ("799", "399") })
        {
            var line = form.AddTreatment();
            line.SetTreatment("Lip filler", KnownItems.None);
            line.Price = price;
            line.RegularPrice = regular;
        }

        Assert.Equal(699m, form.ToPlan(KnownItem.NewId(), KnownItems.None).Plan!.Added.Single().RegularPrice);
    }

    [Fact]
    public async Task A_refused_promotion_adds_no_treatment_and_changes_no_price()
    {
        var (session, store, records) = await Session(Balancing);
        session.NewPromotion.Name = "Holiday filler promo";
        TreatmentLine(session, session.NewPromotion, "Full-Face Facial Balancing", "6000", "5400");
        TreatmentLine(session, session.NewPromotion, "Lip filler", "499");

        Assert.False(await session.AddPromotionAsync());

        Assert.Equal(["'Full-Face Facial Balancing' is $6,000 in the promotion but regularly $5,400: the promotion price must be below the regular price."], session.Errors);
        Assert.Equal([Balancing], (await store.ForClientAsync(Salon)).All);
        Assert.Empty(records.Activity.Events);
    }

    [Fact]
    public async Task A_rename_or_price_change_on_her_list_shows_in_every_saved_promotion_and_says_how_far_it_reaches()
    {
        var spring = Holiday() with { Id = KnownItem.NewId(), Name = "Spring refresh" };
        var (session, _, _) = await Session(Midface, Balancing, Wellness, Holiday(), spring);

        Assert.Equal("Used in 2 saved promotions; they will show the new price.", session.Reach(Balancing));
        Assert.Null(session.Reach(Wellness));

        session.StartEdit(Balancing);
        session.EditTreatment!.Name = "Facial Balancing";
        session.EditTreatment.RegularPrice = "5400";
        Assert.True(await session.SaveEditAsync());

        Assert.Equal(2, session.Items.Promotions.Count);
        Assert.All(session.Items.Promotions, p => Assert.Equal(
            "Facial Balancing — $3,499 (Regularly $5,400, saving $1,901)", p.Shown(session.Items)[1]));
    }

    [Fact]
    public async Task Lowering_a_regular_price_to_a_saved_promotion_s_price_shows_the_problem_on_that_promotion()
    {
        var (session, _, _) = await Session(Midface, Balancing, Holiday());

        session.StartEdit(Balancing);
        session.EditTreatment!.RegularPrice = "3499";
        Assert.True(await session.SaveEditAsync());

        Assert.Equal(["'Full-Face Facial Balancing' is $3,499 in the promotion but regularly $3,499: the promotion price must be below the regular price."],
            session.Items.Promotions.Single().LineProblems(session.Items));
    }

    [Fact]
    public async Task A_saved_promotion_is_changed_and_removed_as_a_tier_is()
    {
        var (session, _, records) = await Session(Midface, Balancing, Holiday());
        var holiday = session.Items.Promotions.Single();

        session.StartEdit(holiday);
        Assert.Equal("Holiday filler promo", session.EditPromotion!.Name);
        Assert.Equal("5100", Assert.IsType<KnownTreatmentLineForm>(session.EditPromotion.Lines[1]).RegularPrice);
        session.EditPromotion.Name = "Holiday filler promo 2026";
        Assert.True(await session.SaveEditAsync());
        Assert.Equal("Changed \"Holiday filler promo 2026\".", session.Message);

        await session.RemoveAsync(session.Items.Promotions.Single());
        Assert.Equal("Removed \"Holiday filler promo 2026\". Campaigns that used it keep their text.", session.Message);

        Assert.Equal([ActivityAction.KnownItemChanged, ActivityAction.KnownItemRemoved], records.Activity.Events.Select(e => e.Action));
        Assert.All(records.Activity.Events, e => Assert.Equal(holiday.Id, e.EntityId));
    }

    // Her table, unreachable for the promotion's own write only.
    private sealed class FailingPromotionWrites : IKnownItemStore
    {
        private readonly InMemoryKnownItems _inner = new();
        public bool Fail { get; set; } = true;

        public Task<KnownItems> ForClientAsync(ClientName client, CancellationToken ct = default) => _inner.ForClientAsync(client, ct);

        public Task AddAsync(ClientName client, KnownItem item, CancellationToken ct = default) =>
            Fail && item is KnownPromotion ? throw new Azure.RequestFailedException(503, "The table service is unavailable.")
                : _inner.AddAsync(client, item, ct);

        public Task UpdateAsync(ClientName client, KnownItem item, CancellationToken ct = default) => _inner.UpdateAsync(client, item, ct);
        public Task RemoveAsync(ClientName client, string id, CancellationToken ct = default) => _inner.RemoveAsync(client, id, ct);
    }

    [Fact]
    public async Task A_promotion_save_that_fails_midway_says_what_joined_her_list_and_leaves_the_session_usable()
    {
        var store = new FailingPromotionWrites();
        var records = new TestRecords(new ManualClock(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)));
        var session = await KnownItemsSession.OpenAsync(store, records.Recorder.For(Salon, Priya));
        session.NewPromotion.Name = "Holiday filler promo";
        TreatmentLine(session, session.NewPromotion, "Lip filler", "499", "699");

        Assert.False(await session.AddPromotionAsync());

        Assert.Equal([KnownItemsSession.SaveFailed], session.Errors);
        Assert.Equal("Added Lip filler to your treatments, regularly $699.", session.Message);
        Assert.NotNull(session.Items.TreatmentNamed("Lip filler"));            // the list shown was read again
        Assert.Empty((await store.ForClientAsync(Salon)).Promotions);
        Assert.Single(session.NewPromotion.Lines);                              // what she typed is still there

        store.Fail = false;
        Assert.True(await session.AddPromotionAsync());
        Assert.Equal("Added \"Holiday filler promo\".", session.Message);
        Assert.Single(session.Items.Promotions);
    }

    [Fact]
    public async Task A_line_to_a_treatment_removed_meanwhile_shows_as_no_longer_on_her_list_and_is_not_saved_unchanged()
    {
        var store = new InMemoryKnownItems();
        store.PutRow(KnownItemTable.FromItem(Salon, Holiday(new KnownTreatmentLine(Balancing.Id, 3499m))));
        var (session, _, _) = await Open(store);

        session.StartEdit(session.Items.Promotions.Single());

        Assert.Equal(KnownPromotion.NoLongerOnYourList, Assert.IsType<KnownTreatmentLineForm>(session.EditPromotion!.Lines[0]).Treatment);
        Assert.False(await session.SaveEditAsync());
        Assert.Equal(["Line 1 is a treatment no longer on your list: change or remove it."], session.Errors);
    }

    #endregion

    #region Using a saved promotion

    private static PromotionBlockEditor PromotionBlock(CampaignEditor editor) => editor.Blocks.OfType<PromotionBlockEditor>().Single();

    private static TextBlockEditor FinePrint(CampaignEditor editor) =>
        editor.Blocks.OfType<TextBlockEditor>().Single(b => b.Type == BlockType.FinePrint);

    private static PricedTreatmentEditor Line(CampaignEditor editor, int index) =>
        Assert.IsType<PricedTreatmentEditor>(PromotionBlock(editor).Promotion.Lines[index]);

    [Fact]
    public void Using_a_saved_promotion_fills_its_parts_with_linked_lines_at_today_s_prices_and_leaves_the_dates()
    {
        var editor = CampaignEditor.Start(Editable);
        var block = PromotionBlock(editor);
        var saved = Known.Promotions[0];

        var use = editor.UsePromotion(block, saved, Known, replaceFinePrint: false);

        Assert.True(use.Used);
        Assert.Empty(use.Said);
        Assert.Equal("Holiday filler promo", block.Promotion.Name.Text);
        Assert.Equal(
            new (string?, string, string, string)[]
            {
                (Midface.Id, "Midface Lift / Cheek Rejuvenation", "1599", "2550"),
                (Balancing.Id, "Full-Face Facial Balancing", "3499", "5100"),
            },
            block.Promotion.Lines.Cast<PricedTreatmentEditor>().Select(l => (l.TreatmentId, l.Treatment.Text, l.Price.Text, l.RegularPrice.Text)));
        Assert.Equal("", block.Promotion.Starts.Text);
        Assert.Equal("", block.Promotion.Ends.Text);
        Assert.Contains(editor.Status().Missing, m => m.Location == "Promotion › Starts");
        Assert.Equal(saved.FinePrint, FinePrint(editor).Field.Text);
    }

    // A benefit line comes in as her saved one says it, and the promotion is then hers, so Save is not offered.
    [Fact]
    public void Using_a_saved_promotion_with_a_benefit_line_fills_that_benefit_and_is_then_one_of_hers()
    {
        var known = new KnownItems([Midface, Holiday(new KnownTreatmentLine(Midface.Id, 1599m), new KnownBenefitLine(new Credit(50m, "your next appointment")))]);
        var editor = CampaignEditor.Start(Editable);
        var block = PromotionBlock(editor);

        Assert.True(editor.UsePromotion(block, known.Promotions[0], known, replaceFinePrint: false).Used);

        var benefit = Assert.IsType<BenefitLineDraft>(editor.Draft.Promotion("Promotion").Lines[1]);
        Assert.Equal("$50 credit toward your next appointment", benefit.Benefit.Value.Describe());
        Assert.True(block.Promotion.IsOneOf(known));
    }

    // Decision 8: the dates are the Promotion block's, said once by the sentence its export ends with.
    [Fact]
    public void A_picked_promotion_s_dates_are_said_once_in_the_export()
    {
        var editor = PromotionTests.FillerPromo(template: Editable);
        var saved = Known.Promotions[0];

        Assert.True(editor.UsePromotion(PromotionBlock(editor), saved, Known, replaceFinePrint: true).Used);

        var preview = EditorExport.Preview(editor.Draft.Build().Campaign!);
        const string dates = "Offer valid November 1 – December 31, 2026.";
        Assert.Equal(1, (preview.Length - preview.Replace(dates, "", StringComparison.Ordinal).Length) / dates.Length);
        Assert.Contains(saved.FinePrint!, preview);
    }

    [Fact]
    public void It_asks_before_replacing_her_lines_or_her_fine_print_and_not_otherwise()
    {
        var editor = CampaignEditor.Start(Editable);
        var block = PromotionBlock(editor);
        var saved = Known.Promotions[0];
        Assert.Equal(new PromotionAsks(null, null), editor.AsksBeforeUsing(block, saved, Known));

        // Anything she wrote is asked about: a name alone, with no lines yet.
        block.Promotion.Name.Text = "My promo";
        Assert.Equal(new PromotionAsks("Replace this promotion with Holiday filler promo?", null), editor.AsksBeforeUsing(block, saved, Known));

        block.Promotion.Name.Text = "";
        block.Promotion.AddTreatment();
        FinePrint(editor).Field.Text = "Mine.";

        Assert.Equal(new PromotionAsks("Replace this promotion with Holiday filler promo?", "Replace the fine print too, or keep yours?"),
            editor.AsksBeforeUsing(block, saved, Known));
        Assert.True(editor.UsePromotion(block, saved, Known, replaceFinePrint: false).Used);
        Assert.Equal("Mine.", FinePrint(editor).Field.Text);
    }

    [Fact]
    public void With_no_fine_print_block_it_says_the_fine_print_was_not_added()
    {
        var noFinePrint = new CampaignTemplate("No fine print", [.. Editable.Blocks.Where(b => b.Type != BlockType.FinePrint)]);
        var editor = CampaignEditor.Start(noFinePrint);

        var use = editor.UsePromotion(PromotionBlock(editor), Known.Promotions[0], Known, replaceFinePrint: true);

        Assert.True(use.Used);
        Assert.Equal(["This campaign has no Fine print block, so the promotion's fine print wasn't added."], use.Said);
    }

    // Decision 11: a Fine print block the template fixes is the template's to change. The saved fine print
    // differs from the template's here, so writing it in would show.
    [Fact]
    public void A_fixed_fine_print_block_is_left_as_the_template_has_it()
    {
        var editor = CampaignEditor.Start(PromotionTests.Template);
        var saved = Known.Promotions[0] with { FinePrint = "Valid at the front desk only." };

        var use = editor.UsePromotion(PromotionBlock(editor), saved, Known, replaceFinePrint: true);

        Assert.True(use.Used);
        Assert.StartsWith("Offer valid for filler treatments booked and performed within those dates.", FinePrint(editor).Field.Text);
        Assert.Equal(["This campaign's fine print is the template's own, so the promotion's fine print wasn't added."], use.Said);
    }

    [Fact]
    public void A_line_whose_treatment_has_no_regular_price_is_left_for_her_to_fill()
    {
        var known = new KnownItems([Wellness, Holiday(new KnownTreatmentLine(Wellness.Id, 99m))]);
        var editor = CampaignEditor.Start(Editable);

        Assert.True(editor.UsePromotion(PromotionBlock(editor), known.Promotions[0], known, replaceFinePrint: false).Used);

        Assert.Equal("", Line(editor, 0).RegularPrice.Text);
        Assert.Contains(editor.Status().Missing, m => m.Location == "Promotion › Line 1 › Regular price");
    }

    // Decision 10: a saved promotion with problems fills nothing, and says what to fix where.
    [Fact]
    public void A_saved_promotion_with_a_problem_is_not_used()
    {
        var editor = CampaignEditor.Start(Editable);
        var block = PromotionBlock(editor);
        var missing = new KnownItems([Midface, Holiday()]);

        var use = editor.UsePromotion(block, missing.Promotions[0], missing, replaceFinePrint: true);

        Assert.False(use.Used);
        Assert.Equal(["Holiday filler promo can't be used until it is fixed on the Known items page.",
            "Line 2 is a treatment no longer on your list: change or remove it."], use.Said);
        Assert.Empty(block.Promotion.Lines);
        Assert.Equal("", block.Promotion.Name.Text);
        Assert.Equal("", FinePrint(editor).Field.Text);

        var priced = new KnownItems([Midface, Balancing with { RegularPrice = 3499m }, Holiday()]);
        var again = editor.UsePromotion(block, priced.Promotions[0], priced, replaceFinePrint: true);

        Assert.False(again.Used);
        Assert.Equal("'Full-Face Facial Balancing' is $3,499 in the promotion but regularly $3,499: the promotion price must be below the regular price.",
            again.Said[1]);
        Assert.Empty(block.Promotion.Lines);

        // Not picked, so nothing is asked about it, whatever she wrote.
        block.Promotion.AddTreatment();
        FinePrint(editor).Field.Text = "Mine.";
        Assert.Equal(new PromotionAsks(null, null), editor.AsksBeforeUsing(block, priced.Promotions[0], priced));
    }

    [Fact]
    public void Editing_what_was_filled_never_changes_the_saved_promotion()
    {
        var editor = CampaignEditor.Start(Editable);
        var saved = Known.Promotions[0];
        editor.UsePromotion(PromotionBlock(editor), saved, Known, replaceFinePrint: false);

        Line(editor, 0).Price.Text = "1";
        PromotionBlock(editor).Promotion.Name.Text = "Changed";

        Assert.Equal(1599m, Assert.IsType<KnownTreatmentLine>(Known.Promotions[0].Lines[0]).Price);
        Assert.Equal("Holiday filler promo", Known.Promotions[0].Name);
        Assert.Equal(1m, ((PricedTreatmentDraft)editor.Draft.Promotion("Promotion").Lines[0]).Price.Value);
    }

    // Decision 6: linking is exact; the regular price is still filled ignoring case.
    [Fact]
    public void A_line_typed_with_exactly_her_treatment_s_name_is_linked_and_any_other_is_not()
    {
        var editor = CampaignEditor.Start(Editable);
        var line = PromotionBlock(editor).Promotion.AddTreatment();

        line.SetTreatment("Full-Face Facial Balancing", Known);
        Assert.Equal(Balancing.Id, line.TreatmentId);
        Assert.Equal(Balancing.Id, ((PricedTreatmentDraft)editor.Draft.Promotion("Promotion").Lines[0]).TreatmentId);

        line.SetTreatment("full-face facial balancing", Known);
        Assert.Null(line.TreatmentId);
        Assert.Equal("5100", line.RegularPrice.Text);
    }

    [Fact]
    public void A_linked_line_renamed_by_hand_or_by_a_suggested_fix_is_no_longer_linked()
    {
        var editor = CampaignEditor.Start(Editable);
        editor.UsePromotion(PromotionBlock(editor), Known.Promotions[0], Known, replaceFinePrint: false);

        Line(editor, 0).SetTreatment("Midface Lift", Known);
        Assert.Null(Line(editor, 0).TreatmentId);

        Assert.Equal(Balancing.Id, Line(editor, 1).TreatmentId);
        var spelling = new Finding(Severity.Warning, "ai-spelling", "Promotion", "Spelling.", Excerpt: "Full-Face Facial Balancing")
            { Suggestion = "Full Face Balancing" };
        Assert.Equal(FixOutcome.Applied, SuggestedFix.Apply(editor, spelling));
        Assert.Equal("Full Face Balancing", Line(editor, 1).Treatment.Text);
        Assert.Null(Line(editor, 1).TreatmentId);
    }

    #endregion

    #region Saving a campaign's promotion

    // Decision 8 and 14: a saved promotion's fine print holds no dates; the strip is a whole leading sentence only.
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

    [Fact]
    public void Save_as_a_known_promotion_is_offered_only_when_the_promotion_is_not_one_of_hers()
    {
        var editor = CampaignEditor.Start(Editable);
        var block = PromotionBlock(editor);

        Assert.False(block.Promotion.IsOneOf(Known));
        Assert.Equal("Fill in the promotion's name first.", editor.PromotionToKnown(block, Known).Problem);

        Assert.True(editor.UsePromotion(block, Known.Promotions[0], Known, replaceFinePrint: true).Used);
        Assert.True(block.Promotion.IsOneOf(Known));

        block.Promotion.Starts.Text = "2026-11-01";                       // the dates are the campaign's, not hers
        Assert.True(block.Promotion.IsOneOf(Known));

        Line(editor, 0).Price.Text = "1499";
        Assert.False(block.Promotion.IsOneOf(Known));
    }

    [Fact]
    public async Task Saving_from_a_campaign_adds_new_treatments_first_then_links_its_lines_and_drops_the_dates_sentence()
    {
        var (_, store, _) = await Session(Midface);
        var known = new KnownItems([Midface]);
        var editor = PromotionTests.FillerPromo(known, Editable);
        FinePrint(editor).Field.Text = "Offer valid November 1 – December 31, 2026. Not valid with other offers.";

        var made = editor.PromotionToKnown(PromotionBlock(editor), known);

        Assert.Null(made.Problem);
        Assert.Equal(["Full-Face Facial Balancing"], made.Added.Select(t => t.Name));
        var said = await KnownItemsSession.SavePromotionFromCampaignAsync(
            store, TestRecords.Unwatched.For(Salon, Actor.Demo), made.Promotion!, made.Added);

        Assert.Equal(["Added Full-Face Facial Balancing to your treatments, regularly $5,100.", "Saved \"Filler Promo\" to your known items."], said);
        var kept = await store.ForClientAsync(Salon);
        var saved = kept.Promotions.Single();
        Assert.Equal([Midface.Id, kept.TreatmentNamed("Full-Face Facial Balancing")!.Id],
            saved.Lines.Cast<KnownTreatmentLine>().Select(l => l.TreatmentId));
        Assert.Equal("Not valid with other offers.", saved.FinePrint);
    }

    [Fact]
    public async Task A_line_another_tab_added_meanwhile_is_linked_to_the_treatment_already_on_her_list()
    {
        var (_, store, _) = await Session(Midface);
        var editor = PromotionTests.FillerPromo(new KnownItems([Midface]), Editable);
        var made = editor.PromotionToKnown(PromotionBlock(editor), new KnownItems([Midface]));
        var other = new KnownTreatment(KnownItem.NewId(), "Full-Face Facial Balancing", 5100m);
        await store.AddAsync(Salon, other);

        await KnownItemsSession.SavePromotionFromCampaignAsync(store, TestRecords.Unwatched.For(Salon, Actor.Demo), made.Promotion!, made.Added);

        var kept = await store.ForClientAsync(Salon);
        Assert.Equal(other.Id, Assert.IsType<KnownTreatmentLine>(kept.Promotions.Single().Lines[1]).TreatmentId);
        Assert.Single(kept.Treatments, t => t.Name == "Full-Face Facial Balancing");
    }

    // Final review, Important 3: what is saved is the cleaned text IsOneOf compares, so the button does not come back.
    // The form's fields clean what she types; a draft can hold text not typed there (one saved before, say).
    [Fact]
    public void A_promotion_saved_from_a_campaign_keeps_its_text_cleaned_and_is_then_one_of_hers()
    {
        var known = new KnownItems([Midface]);
        var editor = PromotionTests.FillerPromo(known, Editable);
        var draft = editor.Draft.Promotion("Promotion");
        draft.Summary.Set("  Two months to refresh.\r\nBook now.  ");
        draft.Audience.Set("   ");
        var block = PromotionBlock(editor);

        var made = editor.PromotionToKnown(block, known);

        Assert.Equal("Two months to refresh.\nBook now.", made.Promotion!.Summary);
        Assert.Null(made.Promotion.Audience);
        Assert.True(block.Promotion.IsOneOf(new KnownItems([Midface, .. made.Added, made.Promotion])));
    }

    // Final review, Minor 3: as on the Promotions tab, the first line naming a new treatment gives its regular price.
    [Fact]
    public void Lines_naming_one_new_treatment_in_a_campaign_keep_the_first_line_s_regular_price()
    {
        var editor = PromotionTests.FillerPromo(KnownItems.None, Editable);
        var again = PromotionBlock(editor).Promotion.AddTreatment();
        again.SetTreatment("Full-Face Facial Balancing", KnownItems.None);
        again.Price.Text = "3,999";
        again.RegularPrice.Text = "5,400";

        var made = editor.PromotionToKnown(PromotionBlock(editor), KnownItems.None);

        Assert.Equal(5100m, made.Added.Single(t => t.Name == "Full-Face Facial Balancing").RegularPrice);
    }

    [Fact]
    public void Saving_a_promotion_with_a_near_miss_line_is_refused_with_did_you_mean()
    {
        var editor = PromotionTests.FillerPromo(Known, Editable);
        Line(editor, 1).SetTreatment("Full-Face Facial Balancin", Known);

        var made = editor.PromotionToKnown(PromotionBlock(editor), Known);

        Assert.Equal("Line 2: Did you mean 'Full-Face Facial Balancing'? It's in your known items.", made.Problem);
        Assert.Null(made.Promotion);
        Assert.Empty(made.Added);
    }

    [Fact]
    public void An_unfinished_line_is_asked_to_be_finished_or_removed_first()
    {
        var editor = PromotionTests.FillerPromo(Known, Editable);
        PromotionBlock(editor).Promotion.AddTreatment();

        Assert.Equal("Fill in or remove its unfinished lines first.", editor.PromotionToKnown(PromotionBlock(editor), Known).Problem);
    }

    // Her table, unreachable: reads nothing, fails every write.
    private sealed class FailingStore : IKnownItemStore
    {
        public Task<KnownItems> ForClientAsync(ClientName client, CancellationToken ct = default) => Task.FromResult(KnownItems.None);
        public Task AddAsync(ClientName client, KnownItem item, CancellationToken ct = default) =>
            throw new Azure.RequestFailedException(503, "The table service is unavailable.");
        public Task UpdateAsync(ClientName client, KnownItem item, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(ClientName client, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // Her table, which fails or refuses (as the given exception) every treatment added; the rest goes through.
    private sealed class TreatmentAddsFail(Exception fault) : IKnownItemStore
    {
        public InMemoryKnownItems Inner { get; } = new();
        public Task<KnownItems> ForClientAsync(ClientName client, CancellationToken ct = default) => Inner.ForClientAsync(client, ct);
        public Task AddAsync(ClientName client, KnownItem item, CancellationToken ct = default) =>
            item is KnownTreatment ? throw fault : Inner.AddAsync(client, item, ct);
        public Task UpdateAsync(ClientName client, KnownItem item, CancellationToken ct = default) => Inner.UpdateAsync(client, item, ct);
        public Task RemoveAsync(ClientName client, string id, CancellationToken ct = default) => Inner.RemoveAsync(client, id, ct);
    }

    // Final review, Important 2: the button works on an unsaved form, so nothing says her campaign is saved, and an
    // add that fails or is refused is always said, with the store's own words for a refusal.
    [Theory]
    [InlineData(false, "Try again in a moment.")]
    [InlineData(true, "That name is too long for your treatments.")]
    public async Task A_new_treatment_that_cannot_be_added_is_said_in_this_button_s_words_and_nothing_is_saved(bool refused, string then)
    {
        var store = new TreatmentAddsFail(refused
            ? new ArgumentException("That name is too long for your treatments.")
            : new Azure.RequestFailedException(503, "The table service is unavailable."));
        await store.Inner.AddAsync(Salon, Midface);
        var editor = PromotionTests.FillerPromo(new KnownItems([Midface]), Editable);
        var made = editor.PromotionToKnown(PromotionBlock(editor), new KnownItems([Midface]));
        var log = new ListLogger<KnownItemsSession>();

        var said = await KnownItemsSession.SavePromotionFromCampaignAsync(
            store, TestRecords.Unwatched.For(Salon, Actor.Demo), made.Promotion!, made.Added, log);

        Assert.Equal(
            [$"Couldn't save Filler Promo to your known items: Full-Face Facial Balancing couldn't be added to your treatments. Your campaign is untouched. {then}"],   // (proposed)
            said);
        Assert.Empty((await store.ForClientAsync(Salon)).Promotions);
        Assert.Equal(refused ? 0 : 1, log.Entries.Count);
        Assert.DoesNotContain(log.Entries, e => e.ToString()!.Contains("Balancing"));
    }

    // Decision 12: a name her saved promotions already have is said, not saved twice; a store failure is said, not thrown.
    [Fact]
    public async Task Saving_a_promotion_named_as_one_of_hers_says_it_is_already_known_and_a_store_failure_is_said_not_thrown()
    {
        var (_, store, _) = await Session(Midface, Balancing, Holiday());

        var said = await KnownItemsSession.SavePromotionFromCampaignAsync(
            store, TestRecords.Unwatched.For(Salon, Actor.Demo), Holiday() with { Id = KnownItem.NewId() }, []);

        Assert.Equal(["\"Holiday filler promo\" is already in your known items."], said);

        var log = new ListLogger<KnownItemsSession>();
        var failed = await KnownItemsSession.SavePromotionFromCampaignAsync(
            new FailingStore(), TestRecords.Unwatched.For(Salon, Actor.Demo), Holiday(), [], log);

        Assert.Equal([KnownItemsSession.CampaignSaveFailed], failed);
        Assert.DoesNotContain("Holiday", Assert.Single(log.Entries).ToString());
    }

    #endregion

    #region Following her list

    // Holiday filler promo used in a new campaign: both lines linked to her treatments, at today's prices.
    private static CampaignEditor WithHoliday()
    {
        var editor = CampaignEditor.Start(Editable);
        Assert.True(editor.UsePromotion(PromotionBlock(editor), Known.Promotions[0], Known, replaceFinePrint: false).Used);
        return editor;
    }

    [Fact]
    public void Following_her_list_takes_each_linked_line_s_current_name_and_price_and_says_what_changed()
    {
        var editor = WithHoliday();
        var known = new KnownItems([Midface with { Name = "Midface Lift" }, Balancing with { RegularPrice = 5400m }, Holiday()]);

        Assert.Equal(
            ["From your treatments: Midface Lift / Cheek Rejuvenation is now called Midface Lift.",
             "From your treatments: Full-Face Facial Balancing is now regularly $5,400 (was $5,100)."],
            editor.FollowKnownTreatments(known));
        Assert.Equal("Midface Lift", Line(editor, 0).Treatment.Text);
        Assert.Equal("5400", Line(editor, 1).RegularPrice.Text);
        // The link stays: a renamed treatment is still hers.
        Assert.Equal(Midface.Id, Line(editor, 0).TreatmentId);
        Assert.Empty(editor.FollowKnownTreatments(known));
    }

    // The sentences for a new name with a new price, and for a price the line did not have.
    [Fact]
    public void Following_a_new_name_and_price_or_a_first_price_says_so()
    {
        var editor = WithHoliday();

        Assert.Equal("From your treatments: Full-Face Facial Balancing is now called Facial Balancing, regularly $5,400 (was $5,100).",
            Line(editor, 1).Follow(Balancing with { Name = "Facial Balancing", RegularPrice = 5400m }));
        Line(editor, 0).RegularPrice.Text = "";
        Assert.Equal("From your treatments: Midface Lift / Cheek Rejuvenation is regularly $2,550.", Line(editor, 0).Follow(Midface));
        Assert.Equal("2550", Line(editor, 0).RegularPrice.Text);
        Assert.Null(Line(editor, 0).Follow(Midface));

        var renamed = WithHoliday();
        Line(renamed, 0).RegularPrice.Text = "";
        Assert.Equal("From your treatments: Midface Lift / Cheek Rejuvenation is now called Midface Lift, regularly $2,550.",
            Line(renamed, 0).Follow(Midface with { Name = "Midface Lift" }));
    }

    [Fact]
    public void A_treatment_on_two_lines_is_said_once()
    {
        var known = new KnownItems([Balancing, Holiday(new KnownTreatmentLine(Balancing.Id, 3499m), new KnownTreatmentLine(Balancing.Id, 2999m))]);
        var editor = CampaignEditor.Start(Editable);
        Assert.True(editor.UsePromotion(PromotionBlock(editor), known.Promotions[0], known, replaceFinePrint: false).Used);

        Assert.Equal(["From your treatments: Full-Face Facial Balancing is now regularly $5,400 (was $5,100)."],
            editor.FollowKnownTreatments(new KnownItems([Balancing with { RegularPrice = 5400m }])));
        Assert.Equal(["5400", "5400"], PromotionBlock(editor).Promotion.Lines.Cast<PricedTreatmentEditor>().Select(l => l.RegularPrice.Text));
    }

    [Fact]
    public void Lines_typed_by_hand_are_never_followed()
    {
        var editor = PromotionTests.FillerPromo(template: Editable);

        Assert.Empty(editor.FollowKnownTreatments(new KnownItems([Balancing with { RegularPrice = 9000m }])));
        Assert.Equal("5,100", Line(editor, 1).RegularPrice.Text);
    }

    // Review Focus 5: her list is followed only for what it has; nothing is taken away.
    [Fact]
    public void A_line_whose_treatment_was_removed_or_lost_its_price_is_left_as_it_is()
    {
        var editor = WithHoliday();

        Assert.Empty(editor.FollowKnownTreatments(new KnownItems([Balancing with { RegularPrice = null }])));
        Assert.Equal(("Midface Lift / Cheek Rejuvenation", "2550"), (Line(editor, 0).Treatment.Text, Line(editor, 0).RegularPrice.Text));
        Assert.Equal("5100", Line(editor, 1).RegularPrice.Text);
    }

    #endregion
}

/// <summary>
/// "Use a saved promotion" on the campaign page, held as its circuit holds it: the questions asked in turn,
/// and what the use says. The class shares one app, so each test names its own treatments and promotion.
/// </summary>
public class KnownPromotionPageTests(ProofreadDemoApp app) : IClassFixture<ProofreadDemoApp>
{
    private const string SavedFinePrint =
        "Offer valid for filler treatments booked and performed within those dates. Not valid with other offers.";

    // Her two treatments and a saved promotion of both, named with the test's own suffix.
    private async Task<KnownPromotion> SeedAsync(int n)
    {
        var midface = new KnownTreatment(KnownItem.NewId(), $"Midface Lift / Cheek Rejuvenation {n}", 2550m);
        var balancing = new KnownTreatment(KnownItem.NewId(), $"Full-Face Facial Balancing {n}", 5100m);
        var saved = new KnownPromotion(KnownItem.NewId(), $"Holiday filler promo {n}", null, null,
            [new KnownTreatmentLine(midface.Id, 1599m), new KnownTreatmentLine(balancing.Id, 3499m)], SavedFinePrint);
        await app.KnownItems.AddAsync(DemoApp.Client, midface);
        await app.KnownItems.AddAsync(DemoApp.Client, balancing);
        await app.KnownItems.AddAsync(DemoApp.Client, saved);
        return saved;
    }

    private async Task<OpenCampaignPage> OpenAsync(CampaignDraft draft)
    {
        var save = await app.Stores.Campaigns(DemoApp.Client, Actor.Demo).SaveDraftAsync(Guid.NewGuid(), "Filler Promo", draft);
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return await OpenCampaignPage.OpenAsync(app, save.Id, FakeProofreader.Clean);
    }

    // What the field with this id holds as the page shows it.
    private static string ValueOf(string html, string id)
    {
        var field = Regex.Match(html, $"<(input|textarea) id=\"{Regex.Escape(id)}\"[^>]*>");
        Assert.True(field.Success, $"one field with the id {id}");
        return WebUtility.HtmlDecode(Regex.Match(field.Value, " value=\"([^\"]*)\"").Groups[1].Value);
    }

    private static CampaignEditor WithFinePrint(CampaignEditor editor, string text)
    {
        editor.Blocks.OfType<TextBlockEditor>().Single(b => b.Type == BlockType.FinePrint).Field.Text = text;
        return editor;
    }

    [Fact]
    public async Task Picking_a_saved_promotion_fills_the_block_and_the_fine_print_with_the_dates_left_empty()
    {
        var saved = await SeedAsync(1);
        await using var page = await OpenAsync(CampaignEditor.Start(KnownPromotionTests.Editable).Draft);

        await page.TypeIntoAsync("promotion-use-saved-1", saved.Name);

        var html = await page.HtmlAsync();
        Assert.Equal(saved.Name, ValueOf(html, "promotion-name-1"));
        Assert.Equal("The email says: Midface Lift / Cheek Rejuvenation 1 — $1,599 (Regularly $2,550, saving $951)",
            RenderedPage.Named(html, "line-sentence-1-l0"));
        Assert.Equal(SavedFinePrint, ValueOf(html, "text-3"));
        Assert.Equal("", ValueOf(html, "promotion-starts-1"));
        Assert.Contains("Promotion › Starts", RenderedPage.Text(html));
    }

    [Fact]
    public async Task Picking_over_her_lines_asks_first_and_keep_mine_changes_nothing()
    {
        var saved = await SeedAsync(2);
        await using var page = await OpenAsync(PromotionTests.FillerPromo(template: KnownPromotionTests.Editable).Draft);
        var before = RenderedPage.Named(await page.HtmlAsync(), "line-sentence-1-l0");

        await page.TypeIntoAsync("promotion-use-saved-1", saved.Name);

        Assert.Contains($"Replace this promotion with {saved.Name}?", RenderedPage.Named(await page.HtmlAsync(), "promotion-replace-question"));
        await page.ClickAsync("promotion-keep");

        var html = await page.HtmlAsync();
        Assert.Equal(before, RenderedPage.Named(html, "line-sentence-1-l0"));
        Assert.Equal("Filler Promo", ValueOf(html, "promotion-name-1"));
        Assert.Equal(0, RenderedPage.Count(html, "promotion-replace-question"));
        Assert.Equal(0, RenderedPage.Count(html, "fine-print-question"));
    }

    [Fact]
    public async Task Picking_over_her_fine_print_asks_and_keeping_hers_fills_only_the_promotion()
    {
        var saved = await SeedAsync(3);
        await using var page = await OpenAsync(WithFinePrint(PromotionTests.FillerPromo(template: KnownPromotionTests.Editable), "Mine.").Draft);

        await page.TypeIntoAsync("promotion-use-saved-1", saved.Name);
        await page.ClickAsync("promotion-replace");

        var asked = await page.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(asked, "promotion-replace-question"));
        Assert.Contains("Replace the fine print too, or keep yours?", RenderedPage.Named(asked, "fine-print-question"));
        await page.ClickAsync("fine-print-keep");

        var html = await page.HtmlAsync();
        Assert.Equal(saved.Name, ValueOf(html, "promotion-name-1"));
        Assert.Equal("The email says: Midface Lift / Cheek Rejuvenation 3 — $1,599 (Regularly $2,550, saving $951)",
            RenderedPage.Named(html, "line-sentence-1-l0"));
        Assert.Equal("Mine.", ValueOf(html, "text-3"));
        Assert.Equal(0, RenderedPage.Count(html, "fine-print-question"));
    }

    // Decision 10 on the page: a saved promotion with a problem is not picked, so nothing is asked about it
    // even over her lines and fine print; it is refused at once.
    [Fact]
    public async Task A_saved_promotion_with_a_problem_is_refused_at_once_without_asking()
    {
        var balancing = new KnownTreatment(KnownItem.NewId(), "Full-Face Facial Balancing 5", 5100m);
        var saved = new KnownPromotion(KnownItem.NewId(), "Holiday filler promo 5", null, null,
            [new KnownTreatmentLine(balancing.Id, 3499m)], SavedFinePrint);
        await app.KnownItems.AddAsync(DemoApp.Client, balancing);
        await app.KnownItems.AddAsync(DemoApp.Client, saved);
        // Its regular price changed since, to the promotion's price: the promotion now has a problem.
        await app.KnownItems.UpdateAsync(DemoApp.Client, balancing with { RegularPrice = 3499m });
        await using var page = await OpenAsync(PromotionTests.FillerPromo(template: KnownPromotionTests.Editable).Draft);
        var before = RenderedPage.Named(await page.HtmlAsync(), "line-sentence-1-l0");

        await page.TypeIntoAsync("promotion-use-saved-1", saved.Name);

        var html = await page.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "promotion-replace-question"));
        Assert.Equal(0, RenderedPage.Count(html, "fine-print-question"));
        Assert.StartsWith("Holiday filler promo 5 can't be used until it is fixed on the Known items page.",
            RenderedPage.Named(html, "promotion-use-message"));
        Assert.Equal(before, RenderedPage.Named(html, "line-sentence-1-l0"));
        Assert.Equal("Filler Promo", ValueOf(html, "promotion-name-1"));
    }

    [Fact]
    public async Task Save_as_a_known_promotion_saves_it_says_so_and_is_then_no_longer_offered()
    {
        const string name = "Filler Promo 6";
        var editor = PromotionTests.FillerPromo(template: KnownPromotionTests.Editable);
        editor.Blocks.OfType<PromotionBlockEditor>().Single().Promotion.Name.Text = name;
        await using var page = await OpenAsync(editor.Draft);
        Assert.Equal(1, RenderedPage.Count(await page.HtmlAsync(), "promotion-save-known-1"));

        await page.ClickAsync("promotion-save-known-1");

        var html = await page.HtmlAsync();
        Assert.Contains($"Saved \"{name}\" to your known items.", RenderedPage.Named(html, "promotion-save-note-1"));
        Assert.Equal(0, RenderedPage.Count(html, "promotion-save-known-1"));
        Assert.NotNull((await app.KnownItems.ForClientAsync(DemoApp.Client)).PromotionNamed(name));
    }

    [Fact]
    public async Task With_no_fine_print_block_the_page_says_so()
    {
        var saved = await SeedAsync(4);
        var noFinePrint = new CampaignTemplate("No fine print",
            [.. KnownPromotionTests.Editable.Blocks.Where(b => b.Type != BlockType.FinePrint)]);
        await using var page = await OpenAsync(CampaignEditor.Start(noFinePrint).Draft);

        await page.TypeIntoAsync("promotion-use-saved-1", saved.Name);

        Assert.Equal("This campaign has no Fine print block, so the promotion's fine print wasn't added.",
            RenderedPage.Named(await page.HtmlAsync(), "promotion-use-message"));
    }

    // ---- Following her list on opening, until approved ----

    private CampaignStore Store => app.Stores.Campaigns(DemoApp.Client, Actor.Demo);

    // Her treatment at $5,100, named with the test's own suffix, and a finished campaign saved with its one
    // line linked to it.
    private async Task<(KnownTreatment Treatment, SaveRef Save)> LinkedAsync(int n)
    {
        var treatment = new KnownTreatment(KnownItem.NewId(), $"Full-Face Facial Balancing {n}", 5100m);
        await app.KnownItems.AddAsync(DemoApp.Client, treatment);
        var editor = PromotionTests.FillerPromo(template: KnownPromotionTests.Editable);
        var promotion = editor.Blocks.OfType<PromotionBlockEditor>().Single().Promotion;
        while (promotion.Lines.Count > 0) promotion.RemoveLine(promotion.Lines[^1]);
        var line = promotion.AddTreatment();
        line.SetTreatment(treatment.Name, new KnownItems([treatment]));
        line.Price.Text = "3499";
        Assert.Equal(treatment.Id, line.TreatmentId);
        var save = await Store.SaveDraftAsync(Guid.NewGuid(), "Filler Promo", editor.Draft);
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return (treatment, save);
    }

    [Fact]
    public async Task An_unapproved_campaign_follows_her_list_on_opening_with_the_note_and_saves_nothing()
    {
        var (treatment, save) = await LinkedAsync(7);
        await app.KnownItems.UpdateAsync(DemoApp.Client, treatment with { RegularPrice = 5400m });

        await using var page = await OpenCampaignPage.OpenAsync(app, save.Id, FakeProofreader.Clean);

        var html = await page.HtmlAsync();
        Assert.Equal($"From your treatments: {treatment.Name} is now regularly $5,400 (was $5,100).", RenderedPage.Named(html, "known-followed"));
        Assert.Contains("(Regularly $5,400, saving $1,901)", RenderedPage.Named(html, "line-sentence-1-l0"));
        Assert.Single(await Store.HistoryAsync(DocumentKind.Draft, save.Id));

        await page.ClickAsync("save-button");

        Assert.Equal(0, RenderedPage.Count(await page.HtmlAsync(), "known-followed"));
        Assert.Equal(2, (await Store.HistoryAsync(DocumentKind.Draft, save.Id)).Count);
        var saved = (await DraftSession.OpenAsync(Store, save.Id))!.Editor;
        Assert.Equal("5400",
            Assert.IsType<PricedTreatmentEditor>(saved.Blocks.OfType<PromotionBlockEditor>().Single().Promotion.Lines[0]).RegularPrice.Text);
    }

    [Fact]
    public async Task An_approved_campaign_is_not_changed_and_the_worth_a_look_stands()
    {
        var (treatment, save) = await LinkedAsync(8);
        await Store.ApproveAsync(save, "Priya");
        await app.KnownItems.UpdateAsync(DemoApp.Client, treatment with { RegularPrice = 5400m });

        await using var page = await OpenCampaignPage.OpenAsync(app, save.Id, FakeProofreader.Clean);

        var html = await page.HtmlAsync();
        Assert.Equal(0, RenderedPage.Count(html, "known-followed"));
        Assert.Contains("(Regularly $5,100, saving $1,601)", RenderedPage.Named(html, "line-sentence-1-l0"));
        Assert.Contains("is regularly $5,400 in your known items; here it is $5,100.", RenderedPage.Named(html, "checks-findings"));
        Assert.Equal(1, RenderedPage.Count(html, "approved"));
    }

    // The note says what following changed as the campaign opened. Undo, Restore and restoring it after a delete
    // put a saved version in place of the form, so the note goes, and her list is not followed again until the
    // campaign is next opened: the line shows the price as that version was saved.
    [Fact]
    public async Task Undo_and_Restore_clear_the_followed_note_and_do_not_follow_again()
    {
        var (treatment, first) = await LinkedAsync(9);
        // Saved again, so Undo has a version before it to open.
        await Store.SaveDraftAsync(first.Id, "Filler Promo", await Store.LoadDraftAsync(first));
        app.Clock.Now += TimeSpan.FromSeconds(1);
        await app.KnownItems.UpdateAsync(DemoApp.Client, treatment with { RegularPrice = 5400m });
        await using var page = await OpenCampaignPage.OpenAsync(app, first.Id, FakeProofreader.Clean);
        Assert.Equal(1, RenderedPage.Count(await page.HtmlAsync(), "known-followed"));

        async Task AsSaved(string after)
        {
            var html = await page.HtmlAsync();
            Assert.True(RenderedPage.Count(html, "known-followed") == 0, $"no note after {after}");
            Assert.Contains("(Regularly $5,100, saving $1,601)", RenderedPage.Named(html, "line-sentence-1-l0"));
        }

        await page.ClickAsync("undo-button");
        await page.ClickAsync("undo-confirm");
        await AsSaved("Undo");

        await page.ClickAsync("restore-button");
        await AsSaved("Restore");

        await page.ClickAsync("delete-button");
        await page.ClickAsync("delete-confirm");
        await page.ClickAsync("restore-deleted");
        await AsSaved("restoring the deleted campaign");
    }
}
