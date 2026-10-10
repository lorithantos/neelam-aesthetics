using System.Net;
using System.Text.RegularExpressions;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>The campaigns list and editor, requested through the enforcing app.</summary>
public class CampaignPagesTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord SalonOne = new(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One");

    private static readonly ClientRecord SalonTwo = new(
        new ClientName("test-salon-two"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "Salon Two");

    private static readonly Guid Membership = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Replayed = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Finished = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid SameNames = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
    private static readonly Guid SalonTwoCampaign = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private const string FinishedLabel = "Beauty Bank -- first send";
    private const string SalonTwoLabel = "Salon two’s label";

    private async Task<(HttpStatusCode Status, string Page)> Get(string path, string[] roles, Guid[]? groups = null)
    {
        if ((await app.Clients.ListAsync()).Count == 0)
        {
            await app.Clients.AddAsync(SalonOne);
            await app.Clients.AddAsync(SalonTwo);
            var stores = app.Stores;
            var one = stores.Campaigns(SalonOne.Name, Actor.Demo);
            await one.SaveTemplateAsync(Membership, DraftFixtures.Membership);
            await one.SaveDraftAsync(Replayed, "Second send, replayed", DraftFixtures.SecondSendReplayed());
            var finished = DraftFixtures.Finished();
            finished.Label = FinishedLabel;
            await one.SaveDraftAsync(Finished, "WE’RE TURNING ONE!", finished);
            await one.SaveDraftAsync(SameNames, "Both tiers Platinum, no terms", DraftFixtures.SameNamesNoTerms());
            // Salon two has a campaign and no templates.
            var salonTwos = DraftFixtures.StartAndFillText();
            salonTwos.Label = SalonTwoLabel;
            await stores.Campaigns(SalonTwo.Name, Actor.Demo).SaveDraftAsync(SalonTwoCampaign, "Salon two’s own campaign", salonTwos);
        }
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        if (roles.Length > 0) client.SignedIn(roles, groups ?? [SalonOne.GroupId]);
        var response = await client.GetAsync(path);
        // Razor encodes characters outside ASCII, such as ’ and emoji.
        return (response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    [Theory]
    [InlineData("/campaigns")]
    [InlineData("/campaigns/new/aaaaaaaa-0000-0000-0000-000000000001")]
    [InlineData("/campaigns/bbbbbbbb-0000-0000-0000-000000000001")]
    public async Task Nobody_signed_in_is_challenged(string path) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(path, [])).Status);

    // Changing layouts and reviewing are granted apart from writing campaigns.
    [Theory]
    [InlineData("/campaigns")]
    [InlineData("/campaigns/new/aaaaaaaa-0000-0000-0000-000000000001")]
    [InlineData("/campaigns/bbbbbbbb-0000-0000-0000-000000000001")]
    public async Task Other_roles_do_not_open_the_campaigns(string path) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(path, [Features.Templates, Features.Review])).Status);

    [Fact]
    public async Task A_member_sees_their_client_s_campaigns_and_templates_and_not_another_client_s()
    {
        var (status, page) = await Get("/campaigns", [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Second send, replayed", page);
        Assert.Contains($"campaigns/{Replayed}", page);
        Assert.Contains($"campaigns/new/{Membership}", page);
        Assert.DoesNotContain("Salon two’s own campaign", page);
        // Her business by the name in the clients table, not its container's.
        // No possessive built from the name: "Neelam Aesthetics's" read badly.
        Assert.Contains("Campaigns for Salon One.", page);
        Assert.DoesNotContain("'s campaigns", page);
        Assert.DoesNotContain("test-salon-one", page);

        var (_, other) = await Get("/campaigns", [Features.Campaigns], groups: [SalonTwo.GroupId]);
        Assert.Contains("Campaigns for Salon Two.", other);
        Assert.Contains("Salon two’s own campaign", other);
        Assert.DoesNotContain("Second send, replayed", other);
        // No templates to start from: it says so, and where to make one.
        Assert.Contains("no templates to start a campaign from yet", other);
        Assert.Contains("href=\"templates/new\"", other);
    }

    // The role is not enough: whose campaigns comes from the client groups, through the access check.
    [Fact]
    public async Task The_role_without_a_client_shows_no_campaigns()
    {
        var (status, page) = await Get("/campaigns", [Features.Campaigns], groups: []);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("not a member of any client", page);
        Assert.DoesNotContain("Second send, replayed", page);
    }

    // Another client's campaign, by its id, is simply not there for this member.
    [Fact]
    public async Task Another_client_s_campaign_is_not_found()
    {
        var (_, page) = await Get($"/campaigns/{SalonTwoCampaign}", [Features.Campaigns]);

        Assert.Contains("<h1>This campaign couldn't be opened</h1>", page);
        Assert.DoesNotContain("Salon two’s own campaign", page);
    }

    [Fact]
    public async Task A_new_campaign_opens_with_the_template_s_fixed_parts_and_empty_fields()
    {
        var (status, page) = await Get($"/campaigns/new/{Membership}", [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Untitled campaign from Membership announcement", page);
        // Fixed parts, as they will be sent.
        Assert.Contains("With gratitude,", page);
        Assert.Contains("Hi Beautiful 🤍", page);
        // Fields for the rest, empty, and what is missing.
        Assert.Contains("id=\"subject\"", page);
        Assert.Contains("Subject has not been filled in.", page);
        Assert.Contains("Headline has not been filled in.", page);
        Assert.Contains("Save campaign", page);
        // The email so far, each part to write marked where it goes.
        Assert.Contains("‹Headline: not filled in yet›", page);
    }

    // After the first save the tab is titled as a reload would title it: the saved campaign's subject,
    // where the new campaign's address said "Untitled campaign from ...". The page sets the tab's
    // title from the session's Title after a save (its head is not interactive, so PageTitle alone
    // leaves the first title standing); that call needs a browser, so here it is the title it is
    // given, and what the saved campaign's own address renders, that are checked.
    [Fact]
    public async Task After_the_first_save_the_title_is_the_saved_campaign_s_subject()
    {
        var (_, fresh) = await Get($"/campaigns/new/{Membership}", [Features.Campaigns]);
        Assert.Contains("<title>Untitled campaign from Membership announcement</title>", fresh);
        var session = (await DraftSession.StartAsync(app.Stores.Campaigns(SalonOne.Name, Actor.Demo), Membership))!;
        session.Editor.Subject.Text = "Our first birthday";

        var saved = await session.SaveAsync();

        Assert.Equal("Our first birthday", session.Editor.Title);
        var (_, reloaded) = await Get($"/campaigns/{saved.Id}", [Features.Campaigns]);
        Assert.Contains($"<title>{session.Editor.Title}</title>", reloaded);
    }

    // What only the list carries.
    private const string TheList = "<h2>Your campaigns</h2>";

    // A campaign's name in the list, as the link to it.
    private static string ListHeading(Guid id, string name) => $"<h3><a href=\"campaigns/{id}\">{name}</a></h3>";

    private static string MustFix(string message) => $"<strong>Must fix</strong>\\s*<span>{Regex.Escape(message)}</span>";

    // Priya's campaign as it went out: no terms link, which blocks, and the checks still run over the rest.
    [Fact]
    public async Task With_a_part_missing_the_page_lists_it_as_must_fix_and_still_shows_the_findings_and_the_preview()
    {
        var (_, page) = await Get($"/campaigns/{SameNames}", [Features.Campaigns]);

        Assert.Matches(MustFix("Offer › Terms link has not been filled in."), page);
        Assert.Contains("What the checks say", page);
        Assert.Contains("Tiers 1 and 2 share the name 'Platinum Member'; customers cannot tell them apart. Rename either one.", page);
        // In the preview, set apart as a placeholder.
        Assert.Matches($"<p class=\"placeholder\">\\s*{Regex.Escape("‹Terms link: not filled in yet›")}\\s*</p>", page);
    }

    // Said on the page itself, under the address that was opened: never the list in its place.
    [Theory]
    [InlineData("/campaigns/new/dddddddd-0000-0000-0000-000000000001", "This template couldn't be opened", "Template not found")]
    [InlineData("/campaigns/dddddddd-0000-0000-0000-000000000001", "This campaign couldn't be opened", "Campaign not found")]
    public async Task What_is_not_there_says_so(string path, string heading, string reason)
    {
        var (status, page) = await Get(path, [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains($"<h1>{heading}</h1>", page);
        Assert.Contains($"{reason}: there is nothing saved here.", page);
        Assert.Contains("<a href=\"campaigns\">Back to campaigns</a>", page);
        Assert.DoesNotContain(TheList, page);
    }

    // The owner's report: clicking a campaign seemed to land on the list. Each campaign in the list
    // links, by its name and by Open, to its own address, and that address is its editor.
    [Fact]
    public async Task Each_campaign_in_the_list_links_to_its_own_editor()
    {
        var (_, list) = await Get("/campaigns", [Features.Campaigns]);
        var store = app.Stores.Campaigns(SalonOne.Name, Actor.Demo);
        var campaigns = await DraftSession.ListAsync(store);
        Assert.NotEmpty(campaigns);

        foreach (var campaign in campaigns)
        {
            Assert.Matches($"<h3><a href=\"campaigns/{campaign.Id}\">[^<]+</a></h3>", list);
            Assert.Contains($"<a class=\"button\" href=\"campaigns/{campaign.Id}\">Open</a>", list);

            var (status, editor) = await Get($"/campaigns/{campaign.Id}", [Features.Campaigns]);
            Assert.Equal(HttpStatusCode.OK, status);
            var title = CampaignEditor.Open(await store.LoadDraftAsync(campaign)).Title;
            Assert.Contains($"<h1>{title}</h1>", editor);
            Assert.Contains("Save campaign", editor);
            Assert.DoesNotContain(TheList, editor);
        }
    }

    // Streamed: the page says it is opening the campaign before the campaign's reads are done, so a
    // click never leaves the list on screen under the campaign's address.
    [Fact]
    public async Task The_editor_says_it_is_opening_the_campaign_before_it_has_read_it()
    {
        var (_, editor) = await Get($"/campaigns/{Finished}", [Features.Campaigns]);

        var opening = editor.IndexOf("<h1>Opening the campaign</h1>", StringComparison.Ordinal);
        Assert.True(opening >= 0, "The page does not say it is opening the campaign.");
        Assert.True(opening < editor.IndexOf("<h1>WE’RE TURNING ONE!</h1>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_saved_campaign_reopens_with_its_values_and_its_unchecked_copy()
    {
        var (_, page) = await Get($"/campaigns/{Replayed}", [Features.Campaigns]);

        Assert.Contains("value=\"WE’RE TURNING ONE!\"", page);
        // Both tiers named as the second send had them.
        Assert.Single(Regex.Matches(page, "value=\"Option 1 Platinum Member\""));
        Assert.Single(Regex.Matches(page, "value=\"Option 2 Platinum Member\""));
        Assert.DoesNotContain("Gold Member", page);
        Assert.Contains("value=\"299\"", page);
        // Tier 2's third benefit was copied and never touched: still marked, with its Confirm.
        Assert.Single(Regex.Matches(page, "Copied from Tier 1, not yet checked."));
        Assert.Contains(">Confirm</button>", page);
        Assert.Contains("was copied from Tier 1 and not reviewed", page);
    }

    [Fact]
    public async Task Missing_parts_come_first_then_the_rule_findings_and_never_an_export()
    {
        var (_, unfinished) = await Get($"/campaigns/{Replayed}", [Features.Campaigns]);
        Assert.Contains("Still to do", unfinished);
        Assert.Matches($"<strong>Must fix</strong>\\s*<span>{Regex.Escape("Offer › Tier 2 › Benefit 3 was copied from Tier 1 and not reviewed")}", unfinished);
        Assert.Contains("Checked so far: the parts filled in.", unfinished);
        // Both options are Platinum, as the second send's were, and the checks say so, worth a look.
        Assert.Contains("Tiers 1 and 2 are both 'Platinum'. Tier names like Bronze, Silver, Gold and Platinum tell readers which is which; give each tier its own.", unfinished);

        var (_, sameNames) = await Get($"/campaigns/{SameNames}", [Features.Campaigns]);

        var (_, finished) = await Get($"/campaigns/{Finished}", [Features.Campaigns]);
        Assert.DoesNotContain("Still to do", finished);
        Assert.DoesNotContain("Checked so far", finished);
        Assert.DoesNotContain("not filled in yet", finished);
        Assert.Contains("What the checks say", finished);
        Assert.Contains("Worth a look", finished);
        Assert.Contains("Join the Beauty Bank", finished);

        foreach (var page in new[] { unfinished, sameNames, finished })
        {
            Assert.Contains("Export comes once the AI proofread is switched on.", page);
            Assert.DoesNotContain("Copy into Square", page);
            Assert.DoesNotContain("clipboard", page);
        }
    }

    // Undo hides a save from every page at once; coming back to the campaign within the grace
    // period offers it back, and nothing was deleted by leaving.
    [Fact]
    public async Task An_undone_campaign_is_gone_from_the_list_and_its_page_offers_restore()
    {
        await Get("/campaigns", [Features.Campaigns]);
        var store = app.Stores.Campaigns(SalonOne.Name, Actor.Demo);
        var onlySave = await store.SaveDraftAsync(Guid.NewGuid(), "Undone, all of it", DraftFixtures.Finished());
        var partly = Guid.NewGuid();
        await store.SaveDraftAsync(partly, "Kept version", DraftFixtures.Finished());
        app.Clock.Now += TimeSpan.FromSeconds(1);
        var undoneVersion = await store.SaveDraftAsync(partly, "Undone version", DraftFixtures.Finished());
        await store.MarkUndoneAsync(onlySave);
        await store.MarkUndoneAsync(undoneVersion);

        var (_, list) = await Get("/campaigns", [Features.Campaigns]);
        var yours = YourCampaigns(list);
        Assert.DoesNotContain("Undone, all of it", yours);
        Assert.DoesNotContain("Undone version", list);
        Assert.Contains("Kept version", yours);
        // Deleted, it waits under Recently deleted; a campaign with a version left is not deleted.
        Assert.Contains("<h3>Undone, all of it</h3>", RecentlyDeleted(list));
        Assert.DoesNotContain("Kept version", RecentlyDeleted(list));

        var (_, gone) = await Get($"/campaigns/{onlySave.Id}", [Features.Campaigns]);
        Assert.Contains("<h1>Undone, all of it</h1>", gone);
        Assert.Contains("This campaign is deleted. You can restore it until", gone);
        Assert.Contains(">Restore</button>", gone);
        Assert.DoesNotContain(TheList, gone);

        var (_, editor) = await Get($"/campaigns/{partly}", [Features.Campaigns]);
        Assert.Contains("Save campaign", editor);
        Assert.Contains("Restore undone save", editor);
    }

    private static string YourCampaigns(string list)
    {
        var start = list.IndexOf(TheList, StringComparison.Ordinal);
        var end = list.IndexOf(DeletedHeading, StringComparison.Ordinal);
        return end < 0 ? list[start..] : list[start..end];
    }

    private const string DeletedHeading = "<h2 id=\"recently-deleted\">Recently deleted</h2>";

    private static string RecentlyDeleted(string list)
    {
        var start = list.IndexOf(DeletedHeading, StringComparison.Ordinal);
        return start < 0 ? "" : list[start..];
    }

    // The walkthrough found no way back from the list once a campaign was deleted: it is listed under
    // Recently deleted, with when it goes for good, and Restore there brings it back and opens it.
    [Fact]
    public async Task A_deleted_campaign_restores_from_the_list()
    {
        await Get("/campaigns", [Features.Campaigns]);
        var store = app.Stores.Campaigns(SalonOne.Name, Actor.Demo);
        var save = await store.SaveDraftAsync(Guid.NewGuid(), "Deleted by a misclick", DraftFixtures.Finished());
        await store.MarkUndoneAsync(save);

        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        client.SignedIn([Features.Campaigns], [SalonOne.GroupId]);
        var list = WebUtility.HtmlDecode(await (await client.GetAsync("/campaigns")).Content.ReadAsStringAsync());
        var deleted = RecentlyDeleted(list);
        Assert.Contains("Deleted campaigns can be restored for a day.", deleted);
        var card = Regex.Match(deleted, "<section class=\"card\" aria-label=\"Deleted: Deleted by a misclick\">.*?</section>", RegexOptions.Singleline).Value;
        Assert.Contains("It will be removed for good", card);
        Assert.Contains("<button type=\"submit\" class=\"button\">Restore</button>", card);
        Assert.DoesNotContain("Deleted by a misclick", YourCampaigns(list));

        var token = Regex.Match(card, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/campaigns", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = $"restore-{save.Id:N}",
            ["__RequestVerificationToken"] = token,
        }));

        Assert.True((int)response.StatusCode is >= 300 and < 400, $"Restore answered {response.StatusCode}.");
        Assert.EndsWith($"campaigns/{save.Id}", response.Headers.Location!.OriginalString);
        Assert.Equal(save.BlobName, (await DraftSession.OpenAsync(store, save.Id))!.Latest!.BlobName);
        var (_, after) = await Get("/campaigns", [Features.Campaigns]);
        Assert.Contains(ListHeading(save.Id, "Deleted by a misclick"), YourCampaigns(after));
        Assert.DoesNotContain("Deleted by a misclick", RecentlyDeleted(after));
    }

    // With no version before it, undoing the save deletes the campaign, and the button says so.
    [Fact]
    public async Task The_only_save_s_undo_is_named_delete()
    {
        await Get("/campaigns", [Features.Campaigns]);
        var store = app.Stores.Campaigns(SalonOne.Name, Actor.Demo);
        var id = Guid.NewGuid();
        await store.SaveDraftAsync(id, "One save", DraftFixtures.Finished());

        var (_, once) = await Get($"/campaigns/{id}", [Features.Campaigns]);
        Assert.Contains(">Delete campaign</button>", once);
        Assert.DoesNotContain(">Undo last save</button>", once);

        app.Clock.Now += TimeSpan.FromSeconds(1);
        await store.SaveDraftAsync(id, "Two saves", DraftFixtures.Finished());
        var (_, twice) = await Get($"/campaigns/{id}", [Features.Campaigns]);
        Assert.Contains(">Undo last save</button>", twice);
        Assert.DoesNotContain(">Delete campaign</button>", twice);
    }

    // The walkthrough could not tell whether the italic "Photo: <name>" under a preview photo went out
    // in the email. The block shows where the photo goes; its name is a note outside the block.
    [Fact]
    public async Task A_photo_s_name_is_a_note_outside_the_email()
    {
        var (_, page) = await Get($"/campaigns/{Finished}", [Features.Campaigns]);
        var preview = page[page.LastIndexOf("<h2>Preview</h2>", StringComparison.Ordinal)..];

        Assert.DoesNotContain("Photo: ", preview);
        foreach (var name in new[] { "Principals toasting", "Principals seated" })
        {
            var note = $"<p class=\"field-help\" data-photo-note>For you, not in the email: the photo \"{name}\". {ImageLibrary.NotInLibrary}</p>";
            var at = preview.IndexOf(note, StringComparison.Ordinal);
            Assert.True(at > 0, $"No note for {name}.");
            // Outside its block: the block that holds the photo has closed before the note.
            var block = preview.LastIndexOf("<div class=\"preview-block", at, StringComparison.Ordinal);
            Assert.Contains("</div>", preview[block..at]);
        }
        Assert.Contains("<span class=\"placeholder\">Photo</span>", preview);
    }

    // Removing a tier or a benefit is a plain button, asked about first, not a red one beside Copy tier.
    [Fact]
    public async Task Remove_tier_and_remove_benefit_are_secondary_buttons()
    {
        var (_, page) = await Get($"/campaigns/{Finished}", [Features.Campaigns]);

        Assert.Contains("<button class=\"button\" aria-label=\"Remove tier 1\">Remove tier</button>", page);
        Assert.Contains("<button class=\"button\" aria-label=\"Remove benefit 1 of tier 1\">Remove benefit</button>", page);
        Assert.DoesNotMatch("class=\"button danger\"[^>]*>Remove", page);
    }

    // Two sends of one email share the subject; her label tells them apart, the subject under it.
    [Fact]
    public async Task The_list_shows_her_label_with_the_subject_under_it()
    {
        var (_, page) = await Get("/campaigns", [Features.Campaigns]);

        Assert.Matches(
            $"{Regex.Escape(ListHeading(Finished, FinishedLabel))}\\s*<p class=\"campaign-subject\">{Regex.Escape("WE’RE TURNING ONE!")}</p>",
            page);
        // A campaign with no label is headed by its subject, as before.
        Assert.Contains(ListHeading(Replayed, "Second send, replayed"), page);
        // Saved at noon UTC on 3 October: shown as five in the morning, Pacific daylight time.
        Assert.Matches(@"Last saved 3 Oct 2026, 5:0\d AM PDT", page);
        Assert.DoesNotContain(" UTC", page);

        // The editor offers the label at the top, filled in.
        var (_, editor) = await Get($"/campaigns/{Finished}", [Features.Campaigns]);
        Assert.Contains("Label (just for you)", editor);
        Assert.Contains($"value=\"{FinishedLabel}\"", editor);
        Assert.True(editor.IndexOf("id=\"label\"", StringComparison.Ordinal) < editor.IndexOf("id=\"subject\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_undone_save_s_label_is_not_shown()
    {
        await Get("/campaigns", [Features.Campaigns]);
        var store = app.Stores.Campaigns(SalonOne.Name, Actor.Demo);
        var id = Guid.NewGuid();
        var kept = DraftFixtures.Finished();
        kept.Label = "Label of the kept save";
        await store.SaveDraftAsync(id, "Relabelled campaign", kept);
        app.Clock.Now += TimeSpan.FromSeconds(1);
        var undone = DraftFixtures.Finished();
        undone.Label = "Label of the undone save";
        await store.MarkUndoneAsync(await store.SaveDraftAsync(id, "Relabelled campaign", undone));

        var (_, list) = await Get("/campaigns", [Features.Campaigns]);

        Assert.Contains(ListHeading(id, "Label of the kept save"), list);
        Assert.DoesNotContain("Label of the undone save", list);
    }

    // Each client's labels are in its own container; listing one client's campaigns reads nothing
    // of another's.
    [Fact]
    public async Task Another_client_s_labels_are_never_read()
    {
        await Get("/campaigns", [Features.Campaigns]);
        var one = app.Containers.For(SalonOne.Name.Value);
        var two = app.Containers.For(SalonTwo.Name.Value);
        one.Reads.Clear();
        two.Reads.Clear();

        var (_, page) = await Get("/campaigns", [Features.Campaigns]);

        Assert.Contains(ListHeading(Finished, FinishedLabel), page);
        Assert.NotEmpty(one.Reads);
        Assert.Empty(two.Reads);
        Assert.DoesNotContain(SalonTwoLabel, page);

        var (_, other) = await Get("/campaigns", [Features.Campaigns], groups: [SalonTwo.GroupId]);
        Assert.Contains(ListHeading(SalonTwoCampaign, SalonTwoLabel), other);
        Assert.DoesNotContain(FinishedLabel, other);
    }

    [Fact]
    public async Task The_client_header_and_how_it_works_lead_to_campaigns()
    {
        var (_, page) = await Get("/how-it-works", []);

        var nav = Regex.Match(page, "<nav class=\"site-nav\".*?</nav>", RegexOptions.Singleline).Value;
        Assert.Contains("href=\"campaigns\"", nav);
        Assert.Contains("Try it: <a href=\"campaigns\">Campaigns</a>.", page);
        Assert.DoesNotContain("Writing campaigns here is being built.", page);
    }
}
