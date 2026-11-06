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
        string?[] bad = [null, "{not json", "[null]", """[{"line":"treatment","treatmentId":"","price":1}]"""];
        var rows = bad.Select(lines =>
        {
            var row = new Azure.Data.Tables.TableEntity(Salon.Value, KnownItem.NewId()) { ["Kind"] = "Promotion", ["Text"] = "Secret" };
            if (lines is not null) row["Lines"] = lines;
            return row;
        }).Append(KnownItemTable.FromItem(Salon, Balancing)).ToList();

        var read = KnownItemTable.ReadAll(Salon, rows, log);

        Assert.Equal([Balancing.Id], read.All.Select(i => i.Id));
        Assert.Equal(4, log.Entries.Count);
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
}
