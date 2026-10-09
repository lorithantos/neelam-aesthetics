using System.Net;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Tier-name ladders (owner, 2026-10-09): metals and gemstones weighted, colours of equal weight unless
/// she orders them, all data with her own set in place of the standard. Two notes, both worth a look:
/// two tiers on one rung, and a higher rung that costs less.
/// </summary>
public class TierLadderTests
{
    private static Campaign Offer(params Tier[] tiers) =>
        new("Hello", [new OfferBlock("Offer", new Offer("Membership", "Join us.", tiers, IsRecurring: true, TermsUrl: null, TiersNote: null))]);

    private static Tier T(string name, decimal price, int credit = 25) => new(name, price, [new BirthdayCredit(credit)]);

    private static ReviewReport Check(Campaign c, TierLadders? ladders = null) =>
        CampaignReview.Check(c, business: ladders is null ? null : new BusinessContext("Salon") { Ladders = ladders });

    private static IReadOnlyList<Finding> Rung(ReviewReport r, string rule) => r.Findings.Where(f => f.Rule == rule).ToList();

    [Fact]
    public void The_same_metal_twice_is_worth_a_look_naming_the_ladder_s_words()
    {
        var report = Check(Offer(T("Platinum Member", 149m), T("Platinum Plus", 299m, 75)));

        var note = Assert.Single(Rung(report, "tier-rung-repeated"));
        Assert.Equal(Severity.Warning, note.Severity);
        Assert.Equal("Offer", note.Location);
        Assert.Equal(
            "Tiers 1 and 2 are both 'Platinum'. Tier names like Bronze, Silver, Gold and Platinum tell readers which is which; " +
            "give each tier its own.",
            note.Message);
        Assert.DoesNotContain(report.Blockers, f => f.Rule.StartsWith("tier"));
    }

    [Fact]
    public void Gold_costing_more_than_platinum_is_worth_a_look()
    {
        var note = Assert.Single(Rung(Check(Offer(T("Gold", 299m), T("Platinum", 149m, 75))), "tier-rung-order"));

        Assert.Equal(Severity.Warning, note.Severity);
        Assert.Equal(
            "'Gold' is $299/month but 'Platinum' is $149/month; Platinum usually costs more than Gold. Swap the names or the prices.",
            note.Message);
    }

    // Rung order against price order, never against position: listed highest price first, the
    // right names draw nothing, and the wrong ones still do.
    [Fact]
    public void Rungs_are_compared_with_prices_in_either_direction()
    {
        Assert.Empty(Rung(Check(Offer(T("Gold", 149m), T("Platinum", 299m, 75))), "tier-rung-order"));
        Assert.Empty(Rung(Check(Offer(T("Platinum", 299m, 75), T("Gold", 149m))), "tier-rung-order"));
        Assert.Single(Rung(Check(Offer(T("Platinum", 149m, 75), T("Gold", 299m))), "tier-rung-order"));
    }

    // Colours tell tiers apart and say nothing about which is dearer, until she orders them.
    [Fact]
    public void Colours_are_equal_weight_so_their_order_draws_nothing_but_a_repeat_does()
    {
        // Rose is listed before Blue, so ordered this would be out of order (next test); equal, it is not.
        Assert.Empty(Rung(Check(Offer(T("Rose", 299m), T("Blue", 149m, 75))), "tier-rung-order"));
        Assert.Empty(Rung(Check(Offer(T("Blue", 149m), T("Rose", 299m, 75))), "tier-rung-order"));
        var repeated = Assert.Single(Rung(Check(Offer(T("Blue Member", 149m), T("Blue Plus", 299m, 75))), "tier-rung-repeated"));
        Assert.Contains("Tier names like Rose, Blue, Green and Purple", repeated.Message);
    }

    [Fact]
    public void Colours_she_has_ordered_are_held_to_their_order()
    {
        var colours = TierLadders.Standard.Ladders.Single(l => l.Name == "Colours");
        var ordered = TierLadders.Standard.Replace(2, colours.WithOrdered(true));

        var note = Assert.Single(Rung(Check(Offer(T("Rose", 299m), T("Blue", 149m, 75)), ordered), "tier-rung-order"));
        Assert.Equal("'Rose' is $299/month but 'Blue' is $149/month; Blue usually costs more than Rose. Swap the names or the prices.", note.Message);
    }

    // Her own ladder works like a standard one, and her order of a standard one is the one used.
    [Fact]
    public void Her_own_ladder_and_her_order_are_the_ones_the_checks_use()
    {
        var mine = TierLadders.Standard.With(new TierLadder("Glow", ["Glow", "Radiance", "Luminous"], ordered: true));
        Assert.Single(Rung(Check(Offer(T("Luminous", 99m), T("Glow", 199m, 75)), mine), "tier-rung-order"));
        Assert.Single(Rung(Check(Offer(T("Radiance Club", 99m), T("Radiance", 199m, 75)), mine), "tier-rung-repeated"));

        // Platinum moved below Gold: now Gold is the dearer one.
        var metals = TierLadders.Standard.Ladders[0];
        var reordered = TierLadders.Standard.Replace(0, metals.Move(metals.Words.ToList().IndexOf("Platinum"), -1));
        Assert.Equal(["Bronze", "Silver", "Platinum", "Gold", "Diamond"], reordered.Ladders[0].Words);
        Assert.Empty(Rung(Check(Offer(T("Gold", 299m), T("Platinum", 149m, 75)), reordered), "tier-rung-order"));
        Assert.Single(Rung(Check(Offer(T("Gold", 149m), T("Platinum", 299m, 75)), reordered), "tier-rung-order"));
    }

    // Her set replaces the standard: a standard ladder she removed is not read at all.
    [Fact]
    public void Her_set_replaces_the_standard_one_removed_ladders_included()
    {
        var withoutMetals = TierLadders.Standard.Without(0);

        Assert.Empty(Rung(Check(Offer(T("Gold", 299m), T("Platinum", 149m, 75)), withoutMetals), "tier-rung-order"));
        Assert.Empty(Check(Offer(T("Platinum Member", 149m), T("Platinum Plus", 299m, 75)), TierLadders.None)
            .Findings.Where(f => f.Rule.StartsWith("tier-rung")));
    }

    [Theory]
    [InlineData("platinum member", "PLATINUM PLUS")]
    [InlineData("Platinums", "Platinum")]
    [InlineData("Option 1: Gold", "Gold-Plus")]
    public void Words_match_whole_ignoring_case_and_plural_endings(string first, string second)
    {
        var metals = TierLadders.Standard.Ladders[0];
        Assert.Equal(metals.RungOf(first), metals.RungOf(second));
        Assert.NotNull(metals.RungOf(first));
    }

    [Fact]
    public void A_word_inside_another_word_is_not_a_rung()
    {
        var metals = TierLadders.Standard.Ladders[0];
        Assert.Null(metals.RungOf("Goldilocks"));
        Assert.Equal(2, metals.RungOf("Silverado Gold"));   // Silverado is not Silver
        Assert.Null(metals.RungOf("Silver and Gold"));      // two rungs in one name: neither
    }

    // Diamond tops both the metals and the gemstones: the ladder most of the tiers' names are on wins,
    // and on a tie the one listed first.
    [Fact]
    public void Diamond_is_read_on_the_ladder_the_other_tiers_share()
    {
        var withGold = TierLadders.Standard.Read(["Gold", "Diamond"])!;
        var withRuby = TierLadders.Standard.Read(["Ruby", "Diamond"])!;
        var alone = TierLadders.Standard.Read(["Diamond", "Diamond Plus"])!;

        Assert.Equal("Metals", withGold.Ladder.Name);
        Assert.Equal("Gemstones", withRuby.Ladder.Name);
        Assert.Equal("Metals", alone.Ladder.Name);
        Assert.Null(TierLadders.Standard.Read(["Gold", "Glow"]));
        // Ruby dearer than Diamond is out of order on gemstones.
        Assert.Single(Rung(Check(Offer(T("Ruby", 299m), T("Diamond", 149m, 75))), "tier-rung-order"));
    }

    // One mistake, one finding: identical names are the Must fix alone; names the same apart from
    // their numbers on one rung get the ladder's note, not the numbers' one as well.
    [Fact]
    public void One_mistake_is_never_three_findings()
    {
        var identical = Check(Offer(T("Platinum Member", 149m), T("Platinum Member", 299m, 75)));
        Assert.Equal(["tier-names-unique"], identical.Findings.Where(f => f.Rule.StartsWith("tier")).Select(f => f.Rule));

        var numbered = Check(Offer(T("Option 1 Platinum Member", 149m), T("Option 2 Platinum Member", 299m, 75)));
        Assert.Equal(["tier-rung-repeated"], numbered.Findings.Where(f => f.Rule.StartsWith("tier")).Select(f => f.Rule));

        // Not on a ladder: the numbers' note, as before.
        var glow = Check(Offer(T("Glow 50", 50m), T("Glow 100", 100m, 75)));
        Assert.Equal(["tier-names-numbered"], glow.Findings.Where(f => f.Rule.StartsWith("tier")).Select(f => f.Rule));
    }

    [Fact]
    public void A_ladder_needs_a_name_and_two_different_words()
    {
        Assert.Equal("A ladder needs a name.", TierLadder.Parse(" ", "Glow, Radiance", true).Problem);
        Assert.Equal("The ladder 'Glow' needs at least two words.", TierLadder.Parse("Glow", "Glow", true).Problem);
        Assert.Equal("The ladder 'Glow' has 'Glows' twice.", TierLadder.Parse("Glow", "Glow, Glows", true).Problem);
        var (ladder, problem) = TierLadder.Parse("Glow", " Glow ,Radiance\nLuminous ", true);
        Assert.Null(problem);
        Assert.Equal(["Glow", "Radiance", "Luminous"], ladder!.Words);
        Assert.Throws<ArgumentException>(() => TierLadders.Standard.With(new TierLadder("metals", ["A", "B"], false)));
    }

    [Fact]
    public void A_move_past_either_end_stays_at_that_end()
    {
        var metals = TierLadders.Standard.Ladders[0];
        Assert.Equal(metals.Words, metals.Move(0, -1).Words);
        Assert.Equal(["Silver", "Gold", "Platinum", "Diamond", "Bronze"], metals.Move(0, 10).Words);
    }

    [Fact]
    public void Ladders_round_trip_as_json_empty_included()
    {
        var mine = TierLadders.Standard.With(new TierLadder("Glow", ["Glow", "Radiance"], ordered: false));
        var back = CampaignJson.DeserializeLadders(CampaignJson.SerializeLadders(mine));

        Assert.Equal(mine.Ladders.Select(l => (l.Name, string.Join("|", l.Words), l.Ordered)),
            back.Ladders.Select(l => (l.Name, string.Join("|", l.Words), l.Ordered)));
        Assert.Empty(CampaignJson.DeserializeLadders(CampaignJson.SerializeLadders(TierLadders.None)).Ladders);
        Assert.Throws<InvalidDataException>(() => CampaignJson.DeserializeLadders("""{"schema":2}"""));
        Assert.Throws<InvalidDataException>(() => CampaignJson.DeserializeLadders(
            """{"schema":2,"ladders":[{"name":"One","words":["Only"],"ordered":true}]}"""));
    }
}

/// <summary>Where ladders are kept: hers in her container, the operator's standard in settings, as for the baseline.</summary>
public class TierLadderStoreTests
{
    private static readonly ClientName Neelam = new("neelam-aesthetics");
    private static readonly ClientName Other = new("other-salon");

    private readonly InMemoryContainers _containers = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private ClientStores Stores => Records.Stores(_containers.For, TimeSpan.FromDays(1));

    private static readonly TierLadders Mine = new([new TierLadder("Glow", ["Glow", "Radiance", "Luminous"], ordered: true)]);

    [Fact]
    public async Task The_standard_ladders_apply_until_the_operator_or_she_saves_others()
    {
        var start = await Stores.LaddersInForceAsync(Neelam);
        Assert.False(start.IsOwn);
        Assert.Same(TierLadders.Standard, start.Ladders);

        var standard = await Stores.StandardLadders().SaveAsync(TierLadders.Standard.Without(2));
        Assert.StartsWith("_standard-ladders/", standard.BlobName);
        Assert.Contains(standard.BlobName, _containers.For("settings").Blobs.Keys);
        var operatorStandard = await Stores.LaddersInForceAsync(Neelam);
        Assert.False(operatorStandard.IsOwn);
        Assert.Equal(["Metals", "Gemstones"], operatorStandard.Ladders.Ladders.Select(l => l.Name));
    }

    [Fact]
    public async Task Her_own_ladders_replace_the_standard_and_live_in_her_container()
    {
        await Stores.StandardLadders().SaveAsync(TierLadders.Standard);
        await Stores.SaveLaddersAsync(Neelam, Mine, Actor.Demo);

        var inForce = await Stores.LaddersInForceAsync(Neelam);

        Assert.True(inForce.IsOwn);
        Assert.Equal(["Glow"], inForce.Ladders.Ladders.Select(l => l.Name));
        Assert.Contains(_containers.For(Neelam.Value).Blobs.Keys, k => k.StartsWith("ladders/"));
        Assert.False((await Stores.LaddersInForceAsync(Other)).IsOwn);
        Assert.Equal(ActivityAction.LaddersSaved, Records.Activity.Events[^1].Action);
    }

    // Every version goes, so an older own set never comes back.
    [Fact]
    public async Task Using_the_standard_ladders_deletes_hers_and_the_standard_applies_again()
    {
        await Stores.SaveLaddersAsync(Neelam, Mine, Actor.Demo);
        _clock.Now += TimeSpan.FromMinutes(1);
        await Stores.SaveLaddersAsync(Neelam, TierLadders.None, Actor.Demo);

        await Stores.UseStandardLaddersAsync(Neelam, Actor.Demo);

        Assert.False((await Stores.LaddersInForceAsync(Neelam)).IsOwn);
        Assert.Empty(_containers.For(Neelam.Value).Blobs);
        Assert.Equal(ActivityAction.LaddersResetToStandard, Records.Activity.Events[^1].Action);
    }
}

/// <summary>The ladders on her Known items page and in her campaign's checks, through the enforcing app.</summary>
public class TierLadderPageTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord SalonOne = new(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One");

    private async Task<string> Page(string path)
    {
        if ((await app.Clients.ListAsync()).Count == 0) await app.Clients.AddAsync(SalonOne);
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        client.SignedIn([Features.Campaigns], [SalonOne.GroupId]);
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_known_items_page_shows_the_ladders_in_force_and_whose_they_are()
    {
        await app.Stores.UseStandardLaddersAsync(SalonOne.Name, Actor.Demo);
        var standard = await Page("/known-items");
        await app.Stores.SaveLaddersAsync(SalonOne.Name,
            new TierLadders([new TierLadder("Glow", ["Glow", "Radiance", "Luminous"], ordered: true)]), Actor.Demo);
        var own = await Page("/known-items");
        await app.Stores.UseStandardLaddersAsync(SalonOne.Name, Actor.Demo);

        Assert.Contains("Tier name ladders", standard);
        Assert.Contains("The standard ladders.", standard);
        Assert.Contains("<span>Platinum</span>", standard);
        Assert.Contains("Equal weight: the words only tell tiers apart.", standard);
        Assert.DoesNotContain("Use the standard ladders", standard);
        Assert.Contains("Your own ladders.", own);
        Assert.Contains("<span>Radiance</span>", own);
        Assert.DoesNotContain("<span>Platinum</span>", own);
        Assert.Contains("Use the standard ladders", own);
    }

    // Her order reaches the campaign's checks: with Platinum moved below Gold, the corrected email's
    // Gold at $149 and Platinum at $299 are out of order.
    [Fact]
    public async Task Her_order_reaches_her_campaign_s_checks()
    {
        var id = Guid.NewGuid();
        await Page("/known-items");
        await app.Stores.Campaigns(SalonOne.Name, Actor.Demo).SaveDraftAsync(id, "Celebrate", DraftFixtures.Finished());
        const string Note = "'Platinum Member' is $299/month but 'Gold Member' is $149/month; Gold usually costs more than Platinum.";

        await app.Stores.UseStandardLaddersAsync(SalonOne.Name, Actor.Demo);
        var standard = await Page($"/campaigns/{id}");
        var metals = TierLadders.Standard.Ladders[0];
        await app.Stores.SaveLaddersAsync(SalonOne.Name, TierLadders.Standard.Replace(0, metals.Move(3, -1)), Actor.Demo);
        var reordered = await Page($"/campaigns/{id}");
        await app.Stores.UseStandardLaddersAsync(SalonOne.Name, Actor.Demo);

        // As booleans, so a failure does not print the whole page.
        Assert.False(standard.Contains(Note), "The standard ladders flagged Gold below Platinum.");
        Assert.True(reordered.Contains(Note), "Her order did not reach the campaign's checks.");
    }
}
