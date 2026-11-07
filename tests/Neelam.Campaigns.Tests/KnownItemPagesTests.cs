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

    // The seeded Full-Face Facial Balancing, which the seeded promotion uses.
    private static readonly string BalancingId = KnownItem.NewId();

    private async Task<(HttpStatusCode Status, string Page)> Get(string path, string[] roles, Guid[]? groups = null)
    {
        if ((await app.Clients.ListAsync()).Count == 0)
        {
            await app.Clients.AddAsync(SalonOne);
            await app.Clients.AddAsync(SalonTwo);
            foreach (var salon in new[] { SalonOne, SalonTwo })
                await app.Stores.Campaigns(salon.Name, Actor.Demo).SaveDraftAsync(Finished, "WE’RE TURNING ONE!", DraftFixtures.Finished());
            await app.KnownItems.AddAsync(SalonOne.Name, new KnownTreatment(KnownItem.NewId(), "Wellness injection"));
            await app.KnownItems.AddAsync(SalonOne.Name, new KnownTreatment(BalancingId, "Full-Face Facial Balancing", 5100m));
            await app.KnownItems.AddAsync(SalonOne.Name, new KnownBenefit(KnownItem.NewId(), new BirthdayCredit(75m)));
            await app.KnownItems.AddAsync(SalonOne.Name, new KnownTier(KnownItem.NewId(), "Diamond Member", 499m, [new BirthdayCredit(100m)]));
            await app.KnownItems.AddAsync(SalonOne.Name, new KnownPromotion(KnownItem.NewId(), "Holiday filler promo", null, null,
                [new KnownTreatmentLine(BalancingId, 3499m)], "Not valid with other offers."));
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

    // Each kind on its own tab (owner, 2026-11-03), each with its items and its way to add one.
    [Fact]
    public async Task The_page_lists_her_items_of_each_kind_with_a_way_to_add_each()
    {
        var (status, treatments) = await Get("/known-items", [Features.Campaigns]);
        var (_, lines) = await Get("/known-items?show=benefit-lines", [Features.Campaigns]);
        var (_, tiers) = await Get("/known-items?show=tiers", [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("What Salon One writes again and again, spelled once.", treatments);
        Assert.Contains("<span>Wellness injection</span>", treatments);
        // A treatment's regular price beside its name when it has one; the box to give a new one hers
        // is folded away until she opens it (owner, 2026-11-06), so only the one line shows.
        Assert.Contains("<span>Full-Face Facial Balancing, regularly $5,100</span>", treatments);
        Assert.DoesNotContain("id=\"new-treatment-price\"", treatments);
        Assert.Contains("data-testid=\"open-add-treatment\">Add a treatment</button>", treatments);
        // A benefit line with its usual amount, and no limit on it: any amount is fine.
        Assert.Contains("<span>$75 birthday credit during your birth month — usually $75, any amount</span>", lines);
        Assert.Contains("data-testid=\"open-add-benefit\">Add a benefit line</button>", lines);
        // The price as the campaign shows it: a tier is a monthly price.
        Assert.Contains("<h3>Diamond Member, $499/month</h3>", tiers);
        Assert.Contains("<li>$100 birthday credit during your birth month</li>", tiers);
        Assert.Contains("data-testid=\"open-add-tier\">Build a tier</button>", tiers);
        Assert.DoesNotContain("id=\"tier-pick-new\"", tiers);
        // The way to add one still comes before what she has.
        Assert.True(treatments.IndexOf("open-add-treatment") < treatments.IndexOf("<span>Wellness injection</span>"));
        Assert.True(lines.IndexOf("open-add-benefit") < lines.IndexOf("<span>$75 birthday credit"));
        Assert.True(tiers.IndexOf("open-add-tier") < tiers.IndexOf("<h3>Diamond Member"));
    }

    [Fact]
    public async Task The_treatments_tab_shows_each_price_or_none_and_where_saved_promotions_use_it()
    {
        var (_, page) = await Get("/known-items", [Features.Campaigns]);

        Assert.Contains("<span>Wellness injection</span> <span class=\"muted\">no regular price yet</span>", page);
        Assert.Contains("<span>Full-Face Facial Balancing, regularly $5,100</span>", page);
        Assert.Contains($"<span class=\"muted\" data-testid=\"used-in-{BalancingId}\">Used in: Holiday filler promo</span>", page);
    }

    [Fact]
    public async Task The_promotions_tab_shows_each_saved_promotion_at_today_s_prices_after_the_way_to_build_one()
    {
        var (_, page) = await Get("/known-items?show=promotions", [Features.Campaigns]);

        Assert.Contains("<h3>Holiday filler promo</h3>", page);
        Assert.Contains("<li>Full-Face Facial Balancing — $3,499 (Regularly $5,100, saving $1,601)</li>", page);
        Assert.Contains("Not valid with other offers.", page);
        Assert.DoesNotContain("promotion-fine-print-help-new", page);
        var add = page.IndexOf("data-testid=\"open-add-promotion\">Build a promotion</button>", StringComparison.Ordinal);
        Assert.True(add > 0, "the way to build one is on the page");
        Assert.True(add < page.IndexOf("<h3>Holiday filler promo</h3>", StringComparison.Ordinal));
    }

    [Fact]
    public void The_tabs_are_in_the_page_s_order_and_promotions_can_be_chosen()
    {
        Assert.Equal(["treatments", "promotions", "benefit-lines", "tiers", "ladders"], KnownItemsTab.All);
        Assert.Equal("promotions", KnownItemsTab.Chosen("promotions", null));
    }

    // ---- One kind at a time (owner, 2026-11-03: "Selection is much easier than scrolling down a long
    // page finding what selections exist"): a tab per kind, each a link to the page's own address.

    // Salon one's kinds, each tab's mark that its section is drawn, and how many she has.
    private static readonly (string Show, string Drawn, string Tab)[] Kinds =
    [
        ("treatments", "aria-label=\"Treatments and services\"", "Treatments (2)"),
        ("promotions", "aria-label=\"Promotions\"", "Promotions (1)"),
        ("benefit-lines", "aria-label=\"Benefit lines\"", "Benefit lines (1)"),
        ("tiers", "aria-label=\"Offers and tiers\"", "Tiers (1)"),
        ("ladders", "id=\"ladders-heading\"", "Tier name ladders"),
    ];

    private static string Tabs(string page) =>
        Regex.Match(page, "<nav class=\"tabs\".*?</nav>", RegexOptions.Singleline).Value;

    // Only the chosen kind is drawn, and only its tab is marked as the one open.
    [Theory]
    [InlineData("treatments")]
    [InlineData("promotions")]
    [InlineData("benefit-lines")]
    [InlineData("tiers")]
    [InlineData("ladders")]
    public async Task Each_tab_draws_only_its_own_kind(string show)
    {
        var (status, page) = await Get($"/known-items?show={show}", [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        foreach (var kind in Kinds)
        {
            if (kind.Show == show) Assert.Contains(kind.Drawn, page);
            else Assert.DoesNotContain(kind.Drawn, page);
        }
        var chosen = Kinds.Single(k => k.Show == show);
        var current = Assert.Single(Regex.Matches(Tabs(page), "<a href=\"([^\"]*)\" aria-current=\"page\">([^<]*)</a>"));
        Assert.Equal(($"known-items?show={show}", chosen.Tab), (current.Groups[1].Value, current.Groups[2].Value));
    }

    // Every tab is offered whichever is open, in the page's order, each kind with how many she has; the
    // ladders with none. A link, so back and refresh keep it.
    [Fact]
    public async Task The_tabs_are_links_in_order_with_how_many_of_each_she_has()
    {
        var (_, page) = await Get("/known-items?show=tiers", [Features.Campaigns]);

        var tabs = Regex.Matches(Tabs(page), "<a href=\"([^\"]*)\"[^>]*>([^<]*)</a>");
        Assert.Equal(Kinds.Select(k => ($"known-items?show={k.Show}", k.Tab)), tabs.Select(t => (t.Groups[1].Value, t.Groups[2].Value)));
        // The chosen kind comes under them.
        Assert.True(page.IndexOf("<nav class=\"tabs\"") < page.IndexOf("aria-label=\"Offers and tiers\""));
        // The header's link to the page is still marked as where she is.
        var nav = Regex.Match(page, "<nav class=\"site-nav\".*?</nav>", RegexOptions.Singleline).Value;
        Assert.Contains("class=\"active\"", Regex.Match(nav, "<a [^>]*href=\"known-items\"[^>]*>").Value);
    }

    // With no choice, or one the page does not know, it opens on Treatments, the first.
    [Theory]
    [InlineData("/known-items")]
    [InlineData("/known-items?show=")]
    [InlineData("/known-items?show=offers")]
    public async Task Without_a_known_choice_the_page_opens_on_treatments(string path)
    {
        var (status, page) = await Get(path, [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("<a href=\"known-items?show=treatments\" aria-current=\"page\">Treatments (2)</a>", Tabs(page));
        Assert.Contains("<span>Wellness injection</span>", page);
        Assert.DoesNotContain("aria-label=\"Benefit lines\"", page);
    }

    // A finding's link opens Benefit lines, where the line and its boxes are: by naming it, and a link
    // written before the tabs, which names only the box (?cap=), as well.
    [Theory]
    [InlineData("/known-items?show=benefit-lines&cap=highest-amount")]
    [InlineData("/known-items?cap=highest-amount")]
    [InlineData("/known-items?show=benefit-lines")]
    public async Task A_finding_s_link_opens_benefit_lines_with_the_line_and_its_box(string path)
    {
        var (_, page) = await Get(path, [Features.Campaigns]);
        var line = Assert.Single((await app.KnownItems.ForClientAsync(SalonOne.Name)).Benefits);

        Assert.Contains("<a href=\"known-items?show=benefit-lines\" aria-current=\"page\">Benefit lines (1)</a>", Tabs(page));
        Assert.Contains($"<li id=\"known-line-{line.Id}\"", page);
        Assert.Contains("data-cap=\"highest-amount\"", page);
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
        var pages = new List<string>();
        foreach (var (show, none) in new[] { ("treatments", "No treatments yet."), ("promotions", "No promotions yet."), ("benefit-lines", "No benefit lines yet."), ("tiers", "No tiers yet.") })
        {
            var (status, page) = await Get($"/known-items?show={show}", [Features.Campaigns], [SalonTwo.GroupId]);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains(none, page);
            // Her own counts on the tabs, none of salon one's.
            foreach (var tab in new[] { "Treatments (0)", "Promotions (0)", "Benefit lines (0)", "Tiers (0)" })
                Assert.Contains($">{tab}</a>", page);
            pages.Add(page);
        }

        Assert.All(pages, page =>
        {
            Assert.DoesNotContain("<span>Wellness injection</span>", page);
            Assert.DoesNotContain("birthday credit", page);
            Assert.DoesNotContain("Holiday filler promo", page);
            // Diamond is also a word on the standard tier-name ladders, which are on a tab of their own.
            Assert.DoesNotContain("Diamond", page);
            Assert.DoesNotContain("<option value=\"", page.Split("</select>")[^1]);
        });
    }

    // Her page, in her layout: a link in the header, and nothing of the operator's.
    [Fact]
    public async Task The_page_is_in_the_client_header_and_carries_no_admin_links()
    {
        var (status, page) = await Get("/known-items", [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        var nav = Regex.Match(page, "<nav class=\"site-nav\".*?</nav>", RegexOptions.Singleline).Value;
        Assert.Contains("href=\"known-items\"", nav);
        Assert.DoesNotContain(RenderedPage.Links(page), href => href.Contains("admin/"));
        Assert.DoesNotContain(">Clients<", page);
        Assert.DoesNotContain("Entra", RenderedPage.Text(page));
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
        KnownItemsSession.OpenAsync(store, (records ?? Records()).Recorder.For(SalonOne.Name, Priya));

    // On a clock of the test's own, never the machine's.
    private static TestRecords Records() => new(new ManualClock(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)));

    // Each change is one event on the trail, by the item's id and who did it, never its text.
    [Fact]
    public async Task Adding_changing_and_removing_are_on_the_activity_trail_without_the_text()
    {
        var (store, records) = (new InMemoryKnownItems(), Records());
        var page = await Open(store, records);

        page.NewTreatment.Name = "Wellness injection";
        await page.AddTreatmentAsync();
        var item = page.Items.Treatments[0];
        page.StartEdit(item);
        page.EditTreatment!.Name = "Wellness Injection";
        await page.SaveEditAsync();
        await page.RemoveAsync(page.Items.Treatments[0]);
        await KnownItemsSession.SaveFromCampaignAsync(store, records.Recorder.For(SalonOne.Name, Priya),
            new KnownTreatment(KnownItem.NewId(), "Botox"));
        // Refused: already known. Nothing is recorded for it.
        page.NewTreatment.Name = "botox";
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

        page.NewTreatment.Name = " Wellness injection ";
        Assert.True(await page.AddTreatmentAsync());
        Assert.Equal("Added \"Wellness injection\".", page.Message);
        Assert.Equal("", page.NewTreatment.Name);
        var added = Assert.Single(page.Items.Treatments);

        page.StartEdit(added);
        page.EditTreatment!.Name = "Wellness Injection";
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

    // Folding a builder away without adding drops what was typed in it and the problems its refused add
    // left, as an add does the first; nothing is stored, and the status line stays.
    [Fact]
    public async Task Cancelling_a_new_item_drops_what_was_typed_and_the_problems_and_stores_nothing()
    {
        var (store, client) = (new InMemoryKnownItems(), SalonOne.Name);
        var page = await Open(store);

        page.NewTreatment.Name = "Dropped treatment";
        Assert.False(await page.AddBenefitAsync());
        Assert.NotEmpty(page.Errors);
        page.CancelNewTreatment();
        Assert.Equal("", page.NewTreatment.Name);
        Assert.Empty(page.Errors);

        page.NewBenefit.Kind = "percent-off";
        page.NewBenefit.Percent = "10";
        page.CancelNewBenefit();
        Assert.Equal(BenefitEditor.Standalone().Percent, page.NewBenefit.Percent);
        Assert.Equal(BenefitEditor.Standalone().Kind, page.NewBenefit.Kind);

        page.NewTier.Name = "Dropped tier";
        page.CancelNewTier();
        Assert.Equal("", page.NewTier.Name);

        page.NewPromotion.Name = "Dropped promotion";
        page.CancelNewPromotion();
        Assert.Equal("", page.NewPromotion.Name);

        Assert.Empty((await store.ForClientAsync(client)).All);
    }

    // The problems shown may be a change form's on a card: folding the builder does not take those away.
    [Fact]
    public async Task Cancelling_a_new_item_leaves_the_problems_of_a_change_in_progress()
    {
        var store = new InMemoryKnownItems();
        var page = await Open(store);
        page.NewTreatment.Name = "Kept";
        Assert.True(await page.AddTreatmentAsync());
        page.StartEdit(Assert.Single(page.Items.Treatments));
        page.EditTreatment!.Name = "";
        Assert.False(await page.SaveEditAsync());
        var refused = page.Errors.ToArray();
        Assert.NotEmpty(refused);

        page.CancelNewTreatment();
        page.CancelNewBenefit();
        page.CancelNewTier();
        page.CancelNewPromotion();

        Assert.Equal(refused, page.Errors);
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

    // ---- A line's caps, in place beside it (owner, 2026-10-09: "Let's add caps and let's make sure
    // the editing of the caps is easy to find and update")

    private static async Task<(InMemoryKnownItems Store, KnownBenefit Line)> FiveToTen()
    {
        var store = new InMemoryKnownItems();
        var line = new KnownBenefit(KnownItem.NewId(), new PercentOff(10, "any qualifying treatments"), [new AmountLimit("Percent", 5, 10)]);
        await store.AddAsync(SalonOne.Name, line);
        return (store, line);
    }

    // Usually, Lowest and Highest are boxes beside the line, holding what is kept; saving them goes
    // through the line's own change: held by the store, one event by the id, the list read again.
    [Fact]
    public async Task Saving_a_line_s_caps_in_place_changes_its_usual_amount_and_limits()
    {
        var ((store, line), records) = (await FiveToTen(), Records());
        var page = await Open(store, records);

        var caps = page.Caps(page.Items.Benefits[0]);
        var boxes = Assert.Single(caps.Fields);
        Assert.Equal(PercentOff.PercentField, boxes.Field);
        Assert.Equal(("10", "5", "10"), (boxes.Usual, boxes.Lowest, boxes.Highest));

        (boxes.Usual, boxes.Lowest, boxes.Highest) = ("8", "6", "12");
        Assert.True(await page.SaveCapsAsync(page.Items.Benefits[0]));

        var kept = Assert.Single((await store.ForClientAsync(SalonOne.Name)).Benefits);
        Assert.Equal(line.Id, kept.Id);
        Assert.Equal("8% off any qualifying treatments — usually 8%, between 6% and 12%", kept.Shown);
        Assert.Equal(kept.Shown, page.Items.Benefits[0].Shown);
        Assert.Empty(page.Errors);
        var again = page.Caps(page.Items.Benefits[0]);
        Assert.Equal("Saved.", again.Saved);
        Assert.Equal(("8", "6", "12"), (again.Fields[0].Usual, again.Fields[0].Lowest, again.Fields[0].Highest));
        var change = Assert.Single(records.Activity.Events);
        Assert.Equal((ActivityAction.KnownItemChanged, line.Id), (change.Action, change.EntityId));
        Assert.DoesNotContain("qualifying", change.ToString());
    }

    // Lowest above highest, or the usual amount outside them, is refused as the line's form refuses
    // it: said beside the boxes, with what she typed still in them, and nothing stored or recorded.
    [Theory]
    [InlineData("10", "10", "5", "The lowest percentage (10%) is above the highest (5%).")]
    [InlineData("12", "5", "10", "The usual percentage (12%) is above the highest (10%).")]
    [InlineData("", "5", "10", "Fill in the usual percentage.")]
    [InlineData("10", "lots", "10", "\"lots\" is not a whole number")]
    public async Task An_invalid_range_is_refused_beside_the_boxes_and_what_she_typed_is_kept(
        string usual, string lowest, string highest, string problem)
    {
        var ((store, line), records) = (await FiveToTen(), Records());
        var page = await Open(store, records);
        var boxes = page.Caps(page.Items.Benefits[0]).Fields[0];

        (boxes.Usual, boxes.Lowest, boxes.Highest) = (usual, lowest, highest);
        Assert.False(await page.SaveCapsAsync(page.Items.Benefits[0]));

        var caps = page.Caps(page.Items.Benefits[0]);
        Assert.StartsWith(problem, Assert.Single(caps.Errors));
        Assert.Null(caps.Saved);
        Assert.Empty(page.Errors);
        Assert.Equal((usual, lowest, highest), (caps.Fields[0].Usual, caps.Fields[0].Lowest, caps.Fields[0].Highest));
        Assert.Equal(line.Shown, Assert.Single((await store.ForClientAsync(SalonOne.Name)).Benefits).Shown);
        Assert.Empty(records.Activity.Events);
    }

    // An empty box is no limit at that end; both empty, any amount.
    [Fact]
    public async Task An_empty_box_clears_that_limit()
    {
        var (store, _) = await FiveToTen();
        var page = await Open(store);
        var boxes = page.Caps(page.Items.Benefits[0]).Fields[0];

        boxes.Highest = " ";
        Assert.True(await page.SaveCapsAsync(page.Items.Benefits[0]));
        Assert.Equal([new AmountLimit("Percent", 5, null)], Assert.Single((await store.ForClientAsync(SalonOne.Name)).Benefits).SetLimits);

        page.Caps(page.Items.Benefits[0]).Fields[0].Lowest = "";
        Assert.True(await page.SaveCapsAsync(page.Items.Benefits[0]));
        var kept = Assert.Single((await store.ForClientAsync(SalonOne.Name)).Benefits);
        Assert.Empty(kept.SetLimits);
        Assert.Equal("10% off any qualifying treatments — usually 10%, any amount", kept.Shown);
    }

    // Each benefit line has its own anchor and its caps beside it, labelled plainly; a finding about
    // its range links here.
    [Fact]
    public async Task The_page_gives_each_benefit_line_an_anchor_and_its_caps_beside_it()
    {
        var (_, page) = await Get("/known-items?show=benefit-lines", [Features.Campaigns]);
        var line = Assert.Single((await app.KnownItems.ForClientAsync(SalonOne.Name)).Benefits);

        var item = Regex.Match(page, $"<li id=\"known-line-{line.Id}\" class=\"known-line\" tabindex=\"-1\">(.*?)</li>", RegexOptions.Singleline);
        Assert.True(item.Success);
        Assert.Contains("<span>$75 birthday credit during your birth month — usually $75, any amount</span>", item.Value);
        var key = $"{line.Id}-amount";
        Assert.Contains($"<label for=\"cap-usual-{key}\">Usually</label>", item.Value);
        Assert.Contains($"<label for=\"cap-lowest-{key}\">Lowest</label>", item.Value);
        Assert.Contains($"<label for=\"cap-highest-{key}\">Highest</label>", item.Value);
        Assert.Matches($"<input id=\"cap-usual-{key}\"[^>]*value=\"75\"", item.Value);
        Assert.Matches($"<input id=\"cap-highest-{key}\"[^>]*placeholder=\"no limit\"", item.Value);
        // Named as a finding's link names them (?cap=), so arriving from one starts in that box.
        Assert.Matches($"<input id=\"cap-lowest-{key}\" data-cap=\"lowest-amount\"", item.Value);
        Assert.Matches($"<input id=\"cap-highest-{key}\" data-cap=\"highest-amount\"", item.Value);
        Assert.Equal(2, Regex.Matches(item.Value, "data-cap=").Count);
        Assert.Contains("Leave Lowest or Highest empty for no limit.", item.Value);
        Assert.Contains(">Save</button>", item.Value);
        // One anchor per line, and only for lines.
        Assert.Single(Regex.Matches(page, "id=\"known-line-"));
    }

    // On the campaign page, a benefit outside her line's range links to that line's caps; nothing else
    // there does. Salon three knows one line, up to 5% off, and the campaign's Platinum tier has 10%.
    [Fact]
    public async Task A_finding_outside_a_line_s_range_links_to_its_caps_on_the_campaign_page()
    {
        await Get("/known-items", [Features.Campaigns]);
        var salonThree = new ClientRecord(new ClientName("test-salon-three"), Guid.Parse("33333333-3333-3333-3333-333333333333"), "Salon Three");
        await app.Clients.AddAsync(salonThree);
        var line = new KnownBenefit(KnownItem.NewId(), new PercentOff(5, "any qualifying treatments"), [new AmountLimit("Percent", null, 5)]);
        await app.KnownItems.AddAsync(salonThree.Name, line);
        var id = Guid.NewGuid();
        await app.Stores.Campaigns(salonThree.Name, Actor.Demo).SaveDraftAsync(id, "Out of range", DraftFixtures.Finished());

        var (status, page) = await Get($"/campaigns/{id}", [Features.Campaigns], [salonThree.GroupId]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("'10% off any qualifying treatments' is outside your usual range for this line (up to 5%).", page);
        var links = Regex.Matches(page, "<a class=\"caps-link\" href=\"([^\"]*)\">Change this line's range</a>");
        Assert.NotEmpty(links);
        Assert.Equal(Regex.Matches(page, "is outside your usual range for this line").Count, links.Count);
        // Above the highest: the link opens Benefit lines and starts her in Highest.
        var link = $"known-items?show=benefit-lines&cap=highest-percent#known-line-{line.Id}";
        Assert.All(links, l => Assert.Equal(link, l.Groups[1].Value));
        // A space between the message and the link, never "...(up to 5%).Change this line's range".
        Assert.Equal(links.Count, Regex.Matches(page, "</span> <a class=\"caps-link\"").Count);
        Assert.DoesNotContain("</span><a class=\"caps-link\"", page);

        // Followed, it lands on the line, with the box it names among the line's own.
        var (_, known) = await Get("/" + link.Split('#')[0], [Features.Campaigns], [salonThree.GroupId]);
        var item = Regex.Match(known, $"<li id=\"known-line-{line.Id}\".*?</li>", RegexOptions.Singleline);
        Assert.True(item.Success);
        Assert.Contains("data-cap=\"highest-percent\"", item.Value);
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

        page.NewTreatment.Name = "  ";
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

/// <summary>The Known items page on the demo, held as its circuit holds it (<see cref="OpenCampaignPage"/>).</summary>
public class KnownItemsTabPageTests(DemoApp app) : IClassFixture<DemoApp>
{
    // What an action says, here an add refused, shows under the tabs, above the kind it is about.
    [Fact]
    public async Task What_an_action_says_shows_under_the_tabs()
    {
        await using var page = await OpenCampaignPage.OpenAsync<Neelam.Web.Components.Pages.KnownItemsPage>(
            app, new Dictionary<string, object?>());

        await page.ClickAsync("open-add-treatment");
        await page.ClickAsync("add-treatment");

        var html = await page.HtmlAsync();
        var said = html.IndexOf("Fill in the treatment", StringComparison.Ordinal);
        Assert.True(said > 0, "the refusal is on the page");
        Assert.InRange(html.IndexOf("<nav class=\"tabs\"", StringComparison.Ordinal), 0, said - 1);
        Assert.True(said < html.IndexOf("aria-label=\"Treatments and services\"", StringComparison.Ordinal));
    }

    private static Task<OpenCampaignPage> OpenPromotions(DemoApp app) =>
        OpenCampaignPage.OpenAtAsync<Neelam.Web.Components.Pages.KnownItemsPage>(
            app, "known-items?show=promotions", new Dictionary<string, object?>());

    [Fact]
    public async Task A_promotion_is_built_on_the_page_and_says_what_joined_her_treatments()
    {
        await using var page = await OpenPromotions(app);

        await page.ClickAsync("open-add-promotion");
        await page.TypeIntoAsync("promotion-name-new", "Built here");
        await page.ClickAsync("promotion-add-treatment-new");
        await page.TypeIntoAsync("known-line-treatment-new-l0", "Brand-new peel 1");
        await page.TypeIntoAsync("known-line-price-new-l0", "99");
        await page.TypeIntoAsync("known-line-regular-new-l0", "150");

        Assert.Contains("The email says: Brand-new peel 1 — $99 (Regularly $150, saving $51)",
            WebUtility.HtmlDecode(await page.WaitForAsync("known-line-sentence-new-l0")));

        await page.ClickAsync("add-promotion");

        var html = WebUtility.HtmlDecode(await page.HtmlAsync());
        Assert.Contains("Added Brand-new peel 1 to your treatments, regularly $150.", html);
        Assert.Contains("<h3>Built here</h3>", html);
        // The builder is folded to its one line again.
        Assert.True(Has(html, "open-add-promotion"));
        Assert.False(HasBox(html, "promotion-name-new"));
    }

    [Fact]
    public async Task Changing_a_used_treatment_says_how_far_it_reaches_and_removing_it_says_which_promotion_uses_it()
    {
        var treatment = new KnownTreatment(KnownItem.NewId(), "Reach test 2", 5100m);
        await app.KnownItems.AddAsync(DemoApp.Client, treatment);
        await app.KnownItems.AddAsync(DemoApp.Client, new KnownPromotion(
            KnownItem.NewId(), "Reach promo 2", null, null, [new KnownTreatmentLine(treatment.Id, 10m)], null));
        await using var page = await OpenCampaignPage.OpenAtAsync<Neelam.Web.Components.Pages.KnownItemsPage>(
            app, "known-items", new Dictionary<string, object?>());

        await page.ClickAsync($"remove-{treatment.Id}");
        Assert.Contains("Used by Reach promo 2. Change or remove that promotion first.", WebUtility.HtmlDecode(await page.HtmlAsync()));

        await page.ClickAsync($"change-{treatment.Id}");
        var html = WebUtility.HtmlDecode(await page.HtmlAsync());
        Assert.Contains("data-testid=\"treatment-reach\">Used in 1 saved promotion; it will show the new price.</p>", html);
    }

    [Fact]
    public async Task A_saved_promotion_at_its_regular_price_shows_the_problem_on_its_card()
    {
        var treatment = new KnownTreatment(KnownItem.NewId(), "Card problem 3", 5100m);
        await app.KnownItems.AddAsync(DemoApp.Client, treatment);
        await app.KnownItems.AddAsync(DemoApp.Client, new KnownPromotion(
            KnownItem.NewId(), "Card promo 3", null, null, [new KnownTreatmentLine(treatment.Id, 3499m)], null));
        await app.KnownItems.UpdateAsync(DemoApp.Client, treatment with { RegularPrice = 3499m });

        await using var page = await OpenPromotions(app);

        var html = WebUtility.HtmlDecode(await page.HtmlAsync());
        var card = html[html.IndexOf("aria-label=\"Card promo 3\"", StringComparison.Ordinal)..];
        card = card[..card.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Contains("'Card problem 3' is $3,499 in the promotion but regularly $3,499: the promotion price must be below the regular price.", card);
    }

    // ---- Each tab's builder is one line until she opens it (owner, 2026-11-06: one common layout on
    // every tab, the builder folded to a single line). Per kind: its tab, the button that opens it, its
    // first box, the buttons it has open, and, where one is typed into, the box whose text Cancel drops.

    public static TheoryData<string, string, string, string, string> Builders => new()
    {
        { "treatments", "open-add-treatment", "new-treatment", "add-treatment", "cancel-add-treatment" },
        { "promotions", "open-add-promotion", "promotion-name-new", "add-promotion", "cancel-add-promotion" },
        { "benefit-lines", "open-add-benefit", "benefit-kind-new", "add-benefit", "cancel-add-benefit" },
        { "tiers", "open-add-tier", "tier-name-new", "add-tier", "cancel-add-tier" },
        { "ladders", "open-add-ladder", "ladder-name", "add-ladder", "cancel-add-ladder" },
    };

    private static Task<OpenCampaignPage> OpenTab(DemoApp app, string show) =>
        OpenCampaignPage.OpenAtAsync<Neelam.Web.Components.Pages.KnownItemsPage>(
            app, $"known-items?show={show}", new Dictionary<string, object?>());

    private static bool Has(string html, string testId) => html.Contains($"data-testid=\"{testId}\"", StringComparison.Ordinal);

    private static bool HasBox(string html, string id) => html.Contains($"id=\"{id}\"", StringComparison.Ordinal);

    // What is in the box whose id this is, as it is drawn: nothing when it carries no text.
    private static string TextIn(string html, string id) =>
        Regex.Match(html, $"id=\"{Regex.Escape(id)}\"[^>]*?value=\"([^\"]*)\"") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : "";

    [Theory]
    [MemberData(nameof(Builders))]
    public async Task Every_tab_starts_with_its_builder_folded_to_one_line(string show, string open, string box, string add, string cancel)
    {
        await using var page = await OpenTab(app, show);

        var html = await page.HtmlAsync();
        Assert.True(Has(html, open), "the one line that opens it is there");
        Assert.False(HasBox(html, box), "none of the form shows");
        Assert.False(Has(html, add) || Has(html, cancel), "neither Add nor Cancel shows");
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public async Task Opening_the_builder_shows_its_form_with_Add_and_Cancel_in_the_same_spot(string show, string open, string box, string add, string cancel)
    {
        await using var page = await OpenTab(app, show);

        await page.ClickAsync(open);

        var html = await page.HtmlAsync();
        Assert.True(HasBox(html, box), "the form is there");
        Assert.True(Has(html, add), "with its Add");
        Assert.True(Has(html, cancel), "and a Cancel");
        Assert.False(Has(html, open), "the one line is replaced by the form");
    }

    [Fact]
    public async Task The_opened_form_comes_before_the_items_she_has()
    {
        await app.KnownItems.AddAsync(DemoApp.Client, new KnownTreatment(KnownItem.NewId(), "Below the form 8", 100m));
        await using var page = await OpenTab(app, "treatments");
        await page.ClickAsync("open-add-treatment");

        var html = await page.HtmlAsync();
        var form = html.IndexOf("id=\"new-treatment\"", StringComparison.Ordinal);
        var item = html.IndexOf("<span>Below the form 8, regularly $100</span>", StringComparison.Ordinal);
        Assert.True(form > 0 && item > form, "the form is first, her item below it");
    }

    [Theory]
    [InlineData("treatments", "open-add-treatment", "new-treatment", "cancel-add-treatment")]
    [InlineData("promotions", "open-add-promotion", "promotion-name-new", "cancel-add-promotion")]
    [InlineData("tiers", "open-add-tier", "tier-name-new", "cancel-add-tier")]
    [InlineData("ladders", "open-add-ladder", "ladder-name", "cancel-add-ladder")]
    public async Task Cancel_folds_the_builder_and_drops_what_she_typed(string show, string open, string box, string cancel)
    {
        await using var page = await OpenTab(app, show);
        await page.ClickAsync(open);
        await page.TypeIntoAsync(box, "Typed then dropped");
        Assert.Equal("Typed then dropped", TextIn(await page.HtmlAsync(), box));

        await page.ClickAsync(cancel);

        var folded = await page.HtmlAsync();
        Assert.True(Has(folded, open));
        Assert.False(HasBox(folded, box));
        await page.ClickAsync(open);
        var reopened = await page.HtmlAsync();
        Assert.True(HasBox(reopened, box), "the form opens again, so an empty box is read, not a missing one");
        Assert.Equal("", TextIn(reopened, box));
    }

    // Changing tab folds an open builder, and coming back finds it folded (owner, 2026-11-06).
    [Fact]
    public async Task Changing_tab_folds_an_open_builder()
    {
        await using var page = await OpenTab(app, "treatments");
        await page.ClickAsync("open-add-treatment");
        Assert.True(HasBox(await page.HtmlAsync(), "new-treatment"));

        await page.NavigateToAsync("known-items?show=promotions");
        var promotions = await page.WaitForAsync(html => html.Contains("aria-label=\"Promotions\"", StringComparison.Ordinal), "the Promotions tab");
        Assert.True(Has(promotions, "open-add-promotion"));

        await page.NavigateToAsync("known-items?show=treatments");
        var back = await page.WaitForAsync(html => html.Contains("aria-label=\"Treatments and services\"", StringComparison.Ordinal), "the Treatments tab");
        Assert.True(Has(back, "open-add-treatment"), "folded, not open as she left it");
        Assert.False(HasBox(back, "new-treatment"));
    }

    // What the builders carry once opened, which the folded line hides from a first look.
    [Fact]
    public async Task Opened_builders_carry_their_labels_and_help()
    {
        await app.KnownItems.AddAsync(DemoApp.Client, new KnownBenefit(KnownItem.NewId(), new PercentOff(10, "any qualifying treatments"), []));
        await using var treatments = await OpenTab(app, "treatments");
        await treatments.ClickAsync("open-add-treatment");
        Assert.Contains("Regular price, in dollars (optional)", await treatments.HtmlAsync());

        await using var promotions = await OpenTab(app, "promotions");
        await promotions.ClickAsync("open-add-promotion");
        Assert.Contains($"data-testid=\"promotion-fine-print-help-new\">{KnownPromotionForm.FinePrintHelp}</p>", WebUtility.HtmlDecode(await promotions.HtmlAsync()));

        await using var tiers = await OpenTab(app, "tiers");
        await tiers.ClickAsync("open-add-tier");
        Assert.True(HasBox(await tiers.HtmlAsync(), "tier-pick-new"));
    }

    [Fact]
    public async Task The_ladders_way_to_add_one_is_above_the_ladders_she_has()
    {
        await using var page = await OpenTab(app, "ladders");
        var folded = await page.HtmlAsync();
        var words = folded.IndexOf("<ol class=\"ladder-words\">", StringComparison.Ordinal);
        Assert.True(words > 0, "the demo has ladders in force");
        Assert.InRange(folded.IndexOf("open-add-ladder", StringComparison.Ordinal), 0, words - 1);

        await page.ClickAsync("open-add-ladder");

        var open = await page.HtmlAsync();
        Assert.InRange(open.IndexOf("id=\"ladder-name\"", StringComparison.Ordinal), 0, open.IndexOf("<ol class=\"ladder-words\">", StringComparison.Ordinal) - 1);
    }

    [Fact]
    public async Task Cancelling_a_refused_ladder_add_takes_its_message_with_it()
    {
        await using var page = await OpenTab(app, "ladders");
        await page.ClickAsync("open-add-ladder");
        await page.ClickAsync("add-ladder");
        Assert.True(Has(await page.HtmlAsync(), "ladders-message"), "the refusal is said");

        await page.ClickAsync("cancel-add-ladder");

        Assert.False(Has(await page.HtmlAsync(), "ladders-message"));
    }

    [Fact]
    public async Task Cancel_on_the_benefit_lines_folds_the_builder()
    {
        await using var page = await OpenTab(app, "benefit-lines");
        await page.ClickAsync("open-add-benefit");

        await page.ClickAsync("cancel-add-benefit");

        var html = await page.HtmlAsync();
        Assert.True(Has(html, "open-add-benefit"));
        Assert.False(HasBox(html, "benefit-kind-new"));
    }

    [Fact]
    public async Task A_treatment_added_folds_the_builder_and_shows_in_the_list_below()
    {
        await using var page = await OpenTab(app, "treatments");
        await page.ClickAsync("open-add-treatment");
        await page.TypeIntoAsync("new-treatment", "Folded after adding 7");

        await page.ClickAsync("add-treatment");

        var html = await page.HtmlAsync();
        Assert.True(Has(html, "open-add-treatment"), "folded again");
        Assert.False(HasBox(html, "new-treatment"));
        Assert.Contains("<span>Folded after adding 7</span>", html);
        // The page's own message is as it was.
        Assert.Contains("Folded after adding 7", WebUtility.HtmlDecode(html[html.IndexOf("role=\"status\"", StringComparison.Ordinal)..]));
    }

    [Fact]
    public async Task A_ladder_added_folds_the_builder()
    {
        await using var page = await OpenTab(app, "ladders");
        await page.ClickAsync("open-add-ladder");
        await page.TypeIntoAsync("ladder-name", "Folded ladder 7");
        await page.TypeIntoAsync("ladder-words", "Low, Mid, High");

        await page.ClickAsync("add-ladder");

        var html = await page.HtmlAsync();
        Assert.True(Has(html, "open-add-ladder"), "folded again");
        Assert.False(HasBox(html, "ladder-name"));
        Assert.Contains("Folded ladder 7", html);
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public async Task An_add_that_is_refused_leaves_the_builder_open_to_fix(string show, string open, string box, string add, string cancel)
    {
        await using var page = await OpenTab(app, show);
        await page.ClickAsync(open);

        await page.ClickAsync(add);

        var html = await page.HtmlAsync();
        Assert.True(HasBox(html, box), "still open");
        Assert.True(Has(html, add) && Has(html, cancel));
        Assert.False(Has(html, open));
    }
}
