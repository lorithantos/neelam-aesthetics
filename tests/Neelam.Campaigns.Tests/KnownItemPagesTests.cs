using System.Net;
using System.Text.RegularExpressions;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Her Known items page, and the pickers in the campaign editor, requested through the enforcing
/// app; and the page's adding, changing and removing, through the session the page binds to (no test
/// drives a Blazor circuit). Salon one knows a treatment, a benefit line and a tier; salon two knows
/// nothing.
/// </summary>
public class KnownItemPagesTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord SalonOne = new(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One");

    private static readonly ClientRecord SalonTwo = new(
        new ClientName("test-salon-two"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "Salon Two");

    private static readonly Guid Finished = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private async Task<(HttpStatusCode Status, string Page)> Get(string path, string[] roles, Guid[]? groups = null)
    {
        if ((await app.Clients.ListAsync()).Count == 0)
        {
            await app.Clients.AddAsync(SalonOne);
            await app.Clients.AddAsync(SalonTwo);
            foreach (var salon in new[] { SalonOne, SalonTwo })
                await app.Stores.Campaigns(salon.Name, Actor.Demo).SaveDraftAsync(Finished, "WE’RE TURNING ONE!", DraftFixtures.Finished());
            await app.KnownItems.AddAsync(SalonOne.Name, new KnownTreatment(KnownItem.NewId(), "Wellness injection"));
            await app.KnownItems.AddAsync(SalonOne.Name, new KnownBenefit(KnownItem.NewId(), new BirthdayCredit(75m)));
            await app.KnownItems.AddAsync(SalonOne.Name, new KnownTier(KnownItem.NewId(), "Diamond Member", 499m, [new BirthdayCredit(100m)]));
        }
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        if (roles.Length > 0) client.SignedIn(roles, groups ?? [SalonOne.GroupId]);
        var response = await client.GetAsync(path);
        return (response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Nobody_signed_in_is_challenged_and_templates_alone_do_not_open_it()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/known-items", [])).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/known-items", [Features.Templates])).Status);
    }

    [Fact]
    public async Task The_page_lists_her_items_of_each_kind_with_a_way_to_add_each()
    {
        var (status, page) = await Get("/known-items", [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("What Salon One writes again and again, spelled once.", page);
        Assert.Contains("<span>Wellness injection</span>", page);
        // A benefit line with its usual amount, and no limit on it: any amount is fine.
        Assert.Contains("<span>$75 birthday credit during your birth month — usually $75, any amount</span>", page);
        // The price as the campaign shows it: a tier is a monthly price.
        Assert.Contains("<h3>Diamond Member, $499/month</h3>", page);
        Assert.Contains("<li>$100 birthday credit during your birth month</li>", page);
        foreach (var add in new[] { "Add treatment", "Add benefit line", "Add tier" })
            Assert.Contains($"{add}</button>", page);
        Assert.Contains("id=\"tier-pick-new\"", page);
    }

    // A known tier's price reads as the campaign's preview and export write it, cents and all.
    [Theory]
    [InlineData("299", "$299/month")]
    [InlineData("149.5", "$149.50/month")]
    public void A_known_tier_s_price_is_shown_as_the_campaign_shows_it(string price, string shown) =>
        Assert.Equal(shown, new KnownTier(KnownItem.NewId(), "Platinum Member",
            decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), []).PriceShown);

    [Fact]
    public async Task Another_client_s_items_are_never_shown()
    {
        var (status, page) = await Get("/known-items", [Features.Campaigns], [SalonTwo.GroupId]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("No treatments yet.", page);
        Assert.Contains("No benefit lines yet.", page);
        Assert.Contains("No tiers yet.", page);
        Assert.DoesNotContain("<span>Wellness injection</span>", page);
        Assert.DoesNotContain("birthday credit", page);
        Assert.DoesNotContain("Diamond", page);
        Assert.DoesNotContain("<option value=\"", page.Split("</select>")[^1]);
    }

    // Her page, in her layout: a link in the header, and nothing of the operator's.
    [Fact]
    public async Task The_page_is_in_the_client_header_and_carries_no_admin_links()
    {
        var (status, page) = await Get("/known-items", [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        var nav = Regex.Match(page, "<nav class=\"site-nav\".*?</nav>", RegexOptions.Singleline).Value;
        Assert.Contains("href=\"known-items\"", nav);
        Assert.DoesNotContain("admin/", page);
        Assert.DoesNotContain(">Clients<", page);
        Assert.DoesNotContain("Entra", page);
    }

    // The campaign editor offers her known items: as suggestion lists while typing, a benefit to pick
    // into each tier, a whole tier, and saving each tier and line. Salon two knows nothing, so it is
    // offered no picker, and none of salon one's items.
    [Fact]
    public async Task The_campaign_editor_offers_her_known_items_to_pick_and_to_save()
    {
        var (_, one) = await Get($"/campaigns/{Finished}", [Features.Campaigns]);
        var (_, two) = await Get($"/campaigns/{Finished}", [Features.Campaigns], [SalonTwo.GroupId]);

        Assert.Contains("<option value=\"Wellness injection\"></option>", one);
        Assert.Contains("<option value=\"$75 birthday credit during your birth month\"></option>", one);
        Assert.Contains("<option value=\"Diamond Member\"></option>", one);
        Assert.Contains("Pick a benefit</label>", one);
        Assert.Contains("Add a known tier</label>", one);
        Assert.Contains($"list=\"{KnownItemListsIds.Tiers}\"", one);
        Assert.Contains($"list=\"{KnownItemListsIds.Treatments}\"", one);
        Assert.Contains("Save as a known item</button>", one);

        Assert.DoesNotContain("Wellness injection\"></option>", two);
        Assert.DoesNotContain("Pick a benefit</label>", two);
        Assert.DoesNotContain("Add a known tier</label>", two);
    }

    // The walkthrough was offered to save "Wellness Injecton" with "Wellness injection" known. Save is
    // offered beside the neutral note only: a near miss gets "Did you mean" and no Save.
    [Fact]
    public async Task The_editor_offers_to_save_a_new_treatment_and_never_a_near_miss()
    {
        await Get("/known-items", [Features.Campaigns]);
        var draft = DraftFixtures.Finished();
        draft.Offer("Offer").Tiers[1].AddBenefit(new FreeItem(1, "Wellness Injecton", "per month"));
        draft.Offer("Offer").Tiers[1].AddBenefit(new FreeItem(1, "Hydrafacial", "per month"));
        var id = Guid.NewGuid();
        await app.Stores.Campaigns(SalonOne.Name, Actor.Demo).SaveDraftAsync(id, "Near misses", draft);

        var (_, page) = await Get($"/campaigns/{id}", [Features.Campaigns]);

        Assert.Contains("Did you mean 'Wellness injection'? It's in your known items.", page);
        Assert.DoesNotContain("Save \"Wellness Injecton\" as a known treatment", page);
        Assert.Contains("'Hydrafacial' isn't one of your known treatments.", page);
        Assert.Contains("Save \"Hydrafacial\" as a known treatment</button>", page);
    }

    // ---- What the page does: add, change and remove each kind

    private static readonly Actor Priya = new("Priya Sharma", FromSignIn: true);

    // The page's session for salon one, recording onto a trail the test can read.
    private static Task<KnownItemsSession> Open(IKnownItemStore store, TestRecords? records = null) =>
        KnownItemsSession.OpenAsync(store, (records ?? new TestRecords(TimeProvider.System)).Recorder.For(SalonOne.Name, Priya));

    // Each change is one event on the trail, by the item's id and who did it, never its text.
    [Fact]
    public async Task Adding_changing_and_removing_are_on_the_activity_trail_without_the_text()
    {
        var (store, records) = (new InMemoryKnownItems(), new TestRecords(TimeProvider.System));
        var page = await Open(store, records);

        page.NewTreatment = "Wellness injection";
        await page.AddTreatmentAsync();
        var item = page.Items.Treatments[0];
        page.StartEdit(item);
        page.EditTreatment = "Wellness Injection";
        await page.SaveEditAsync();
        await page.RemoveAsync(page.Items.Treatments[0]);
        await KnownItemsSession.SaveFromCampaignAsync(store, records.Recorder.For(SalonOne.Name, Priya),
            new KnownTreatment(KnownItem.NewId(), "Botox"));
        // Refused: already known. Nothing is recorded for it.
        page.NewTreatment = "botox";
        await page.AddTreatmentAsync();

        var events = records.Activity.Events;
        Assert.Equal(
            [ActivityAction.KnownItemAdded, ActivityAction.KnownItemChanged, ActivityAction.KnownItemRemoved, ActivityAction.KnownItemAdded],
            events.Select(e => e.Action));
        Assert.All(events, e =>
        {
            Assert.Equal(ActivityEntity.KnownItem, e.Entity);
            Assert.Equal(SalonOne.Name, e.Client);
            Assert.Equal("Priya Sharma", e.Actor);
            Assert.Null(e.SaveStamp);
        });
        Assert.Equal(item.Id, events[0].EntityId);
        Assert.DoesNotContain(events, e => e.ToString().Contains("Wellness", StringComparison.OrdinalIgnoreCase)
                                           || e.ToString().Contains("Botox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_page_adds_changes_and_removes_a_treatment()
    {
        var (store, client) = (new InMemoryKnownItems(), SalonOne.Name);
        var page = await Open(store);

        page.NewTreatment = " Wellness injection ";
        Assert.True(await page.AddTreatmentAsync());
        Assert.Equal("Added \"Wellness injection\".", page.Message);
        Assert.Equal("", page.NewTreatment);
        var added = Assert.Single(page.Items.Treatments);

        page.StartEdit(added);
        page.EditTreatment = "Wellness Injection";
        Assert.True(await page.SaveEditAsync());
        Assert.Null(page.Editing);
        Assert.Equal("Wellness Injection", Assert.Single((await store.ForClientAsync(client)).Treatments).Name);

        await page.RemoveAsync(page.Items.Treatments[0]);
        Assert.Empty(page.Items.All);
        Assert.Empty((await store.ForClientAsync(client)).All);
    }

    [Fact]
    public async Task The_page_adds_changes_and_removes_a_benefit_line()
    {
        var (store, client) = (new InMemoryKnownItems(), SalonOne.Name);
        var page = await Open(store);

        page.NewBenefit.Kind = "percent-off";
        page.NewBenefit.Percent = "10";
        page.NewBenefit.AppliesTo = "any qualifying treatments";
        Assert.True(await page.AddBenefitAsync());
        Assert.Null(page.NewBenefit.Value);
        var added = Assert.Single(page.Items.Benefits);
        Assert.Equal("10% off any qualifying treatments", added.Text);

        page.StartEdit(added);
        page.EditBenefit!.Percent = "15";
        Assert.True(await page.SaveEditAsync());
        Assert.Equal(new PercentOff(15, "any qualifying treatments"), Assert.Single((await store.ForClientAsync(client)).Benefits).Benefit);

        await page.RemoveAsync(page.Items.Benefits[0]);
        Assert.Empty((await store.ForClientAsync(client)).All);
    }

    // Each amount's usual value is the benefit's own; its lowest and highest are optional, shown
    // plainly, kept when the line is changed, and refused when they do not hold together.
    [Fact]
    public async Task The_page_sets_a_benefit_line_s_usual_amount_and_its_limits()
    {
        var (store, client) = (new InMemoryKnownItems(), SalonOne.Name);
        var page = await Open(store);

        page.NewBenefit.Kind = "percent-off";
        page.NewBenefit.Percent = "10";
        page.NewBenefit.AppliesTo = "any qualifying treatments";
        var limits = Assert.Single(page.NewLimits.FieldsFor(page.NewBenefit));
        Assert.Equal(PercentOff.PercentField, limits.Field);
        limits.Lowest = "10";
        limits.Highest = "5";
        Assert.False(await page.AddBenefitAsync());
        Assert.Equal(["The lowest percentage (10%) is above the highest (5%)."], page.Errors);
        Assert.Empty((await store.ForClientAsync(client)).All);
        Assert.Equal("10", page.NewBenefit.Percent);

        limits.Lowest = "5%";
        limits.Highest = "10";
        Assert.True(await page.AddBenefitAsync());
        var added = Assert.Single(page.Items.Benefits);
        Assert.Equal("10% off any qualifying treatments — usually 10%, between 5% and 10%", added.Shown);
        Assert.Equal([new AmountLimit("Percent", 5, 10)], added.SetLimits);

        // Changing it starts from what is kept; the usual amount above the highest is refused.
        page.StartEdit(added);
        var editing = Assert.Single(page.EditLimits!.FieldsFor(page.EditBenefit!));
        Assert.Equal(("5", "10"), (editing.Lowest, editing.Highest));
        page.EditBenefit!.Percent = "12";
        Assert.False(await page.SaveEditAsync());
        Assert.Equal(["The usual percentage (12%) is above the highest (10%)."], page.Errors);
        editing.Highest = "";
        Assert.True(await page.SaveEditAsync());
        Assert.Equal("12% off any qualifying treatments — usually 12%, at least 5%",
            Assert.Single((await store.ForClientAsync(client)).Benefits).Shown);
    }

    [Fact]
    public async Task The_page_builds_a_tier_from_picked_and_typed_lines_then_changes_and_removes_it()
    {
        var (store, client) = (new InMemoryKnownItems(), SalonOne.Name);
        var birthday = new KnownBenefit(KnownItem.NewId(), new BirthdayCredit(75m));
        await store.AddAsync(client, birthday);
        var page = await Open(store);

        page.NewTier.Name = "Platinum Member";
        page.NewTier.Price = "$299";
        page.NewTier.AddKnown(page.Items.BenefitNamed("$75 birthday credit during your birth month")!);
        var typed = page.NewTier.AddLine();
        typed.Kind = "free-item";
        typed.Quantity = "1";
        typed.ItemName = "wellness injection";
        typed.Per = "per visit";
        Assert.True(await page.AddTierAsync());

        var tier = Assert.Single((await store.ForClientAsync(client)).Tiers);
        Assert.Equal(("Platinum Member", 299m), (tier.Name, tier.Price));
        Assert.Equal([new BirthdayCredit(75m), new FreeItem(1, "wellness injection", "per visit")], tier.Benefits);
        Assert.Empty(page.NewTier.Lines);

        page.StartEdit(page.Items.Tiers[0]);
        page.EditTier!.Price = "319";
        page.EditTier.RemoveLine(page.EditTier.Lines[1]);
        Assert.True(await page.SaveEditAsync());
        tier = Assert.Single((await store.ForClientAsync(client)).Tiers);
        Assert.Equal(319m, tier.Price);
        Assert.Equal([new BirthdayCredit(75m)], tier.Benefits);

        await page.RemoveAsync(page.Items.Tiers[0]);
        Assert.Equal([birthday.Id], (await store.ForClientAsync(client)).All.Select(i => i.Id));
    }

    [Fact]
    public async Task The_page_says_what_is_wrong_and_keeps_what_was_typed()
    {
        var page = await Open(new InMemoryKnownItems());

        page.NewTier.Name = "Gold";
        page.NewTier.Price = "lots";
        page.NewTier.AddLine();
        Assert.False(await page.AddTierAsync());
        Assert.Contains("\"lots\" is not an amount in dollars, such as 149 or 149.50.", page.Errors);
        Assert.Contains("Benefit 1: fill it in, or remove it.", page.Errors);
        Assert.Equal("Gold", page.NewTier.Name);

        page.NewTreatment = "  ";
        Assert.False(await page.AddTreatmentAsync());
        Assert.Contains("Fill in the treatment's name.", page.Errors);
        Assert.Empty(page.Items.All);
    }

    private static class KnownItemListsIds
    {
        public const string Tiers = Neelam.Web.Components.Shared.KnownItemLists.Tiers;
        public const string Treatments = Neelam.Web.Components.Shared.KnownItemLists.Treatments;
    }
}
