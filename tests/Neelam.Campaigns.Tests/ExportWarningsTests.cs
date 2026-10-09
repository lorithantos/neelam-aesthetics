using System.Net;
using System.Text.RegularExpressions;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// "Worth a look" at export (owner, 2026-10-09): "We should pop up a list of warnings when exporting
/// without explicitly addressing items like '110% off of botox'", and then "This is handholding, not
/// handcuffs." So before the export of an approved version, every warning is listed, each with a way
/// to its field, and one button goes on. Going on is recorded with that version's approval, who and
/// when only, and the list is not shown again for it; a new save shows it again, unless only her label
/// changed. A Must fix is never gone on past.
/// </summary>
public class ExportWarningsTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);
    private static readonly Actor Asha = new("Asha Patel", FromSignIn: true);

    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);

    private CampaignStore StoreFor(Actor actor) => Records.Campaigns(_container, Grace, actor: actor);

    private CampaignStore Store => StoreFor(Actor.Demo);

    // A finished campaign with something worth a look in it ("Beauty Bank", kept on purpose), saved
    // once, approved, and open as the page would have it.
    private async Task<DraftSession> Approved(CampaignStore? store = null)
    {
        store ??= Store;
        var id = Guid.NewGuid();
        await store.SaveDraftAsync(id, "WE’RE TURNING ONE!", DraftFixtures.Finished());
        _clock.Now += TimeSpan.FromMinutes(1);
        var session = (await DraftSession.OpenAsync(store, id))!;
        await session.ApproveAsync("Priya");
        _clock.Now += TimeSpan.FromMinutes(1);
        return session;
    }

    private static Campaign Finished() => DraftFixtures.Finished().Build().Campaign!;

    private static readonly Approval ByPriya = new("Priya", new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    // ---- The gate ----

    // Approved and nothing to fix, and still no export of either kind while what is worth a look has
    // not been shown: the same report refuses both with the same reason.
    [Fact]
    public void No_export_while_any_warning_has_not_been_shown()
    {
        var report = CampaignGate.DemoReview(Finished(), ByPriya);

        Assert.Empty(report.Blockers);
        Assert.NotEmpty(report.Warnings);
        Assert.True(report.WarningsToSee);
        Assert.False(report.CanExport);
        var copy = Assert.Throws<CampaignBlockedException>(() => EditorExport.Blocks(report));
        var json = Assert.Throws<CampaignBlockedException>(() => AssistantExport.Build(report));
        Assert.Equal(copy.Message, json.Message);
        Assert.Null(AssistantExport.Offer(report).Json);
    }

    // Shown and gone on past: both forms of the export, from the same report.
    [Fact]
    public void Once_shown_and_gone_on_past_both_exports_are_there()
    {
        var seen = new WarningsSeen("Priya", ByPriya.At.AddMinutes(2)).For(Finished());
        var report = CampaignGate.DemoReview(Finished(), ByPriya with { WarningsSeen = seen });

        Assert.False(report.WarningsToSee);
        Assert.True(report.CanExport);
        Assert.Equal(seen, report.WarningsSeen);
        Assert.Equal(EditorExport.Blocks(report).Count, AssistantExport.Build(report).Blocks.Count);
    }

    // Going on past the warnings never passes a Must fix, and a Must fix is not listed to go on past.
    [Fact]
    public void A_must_fix_is_never_gone_on_past()
    {
        var draft = DraftFixtures.Finished();
        draft.Offer("Offer").Tiers[0].Name.Set("Platinum Member");
        var unseen = CampaignGate.DemoReview(draft.Build().Campaign!, ByPriya);
        Assert.NotEmpty(unseen.Warnings);
        Assert.False(unseen.WarningsToSee);
        var report = CampaignGate.DemoReview(draft.Build().Campaign!,
            ByPriya with { WarningsSeen = new WarningsSeen("Priya", ByPriya.At).For(draft.Build().Campaign!) });

        Assert.NotEmpty(report.Blockers);
        Assert.False(report.WarningsToSee);
        Assert.False(report.CanExport);
        Assert.Contains("blocking problem", Assert.Throws<CampaignBlockedException>(() => EditorExport.Blocks(report)).Message);
    }

    // ---- The session, as the page drives it ----

    [Fact]
    public async Task Every_warning_is_listed_before_the_export_and_export_anyway_goes_on()
    {
        var session = await Approved();
        var review = CampaignGate.DemoReview(session.Editor.Status().Review!.Campaign, session.CurrentApproval!);

        // Every one, as the checks give them; no export until she goes on.
        Assert.Equal(review.Warnings, session.WarningsBeforeExport());
        Assert.Null(session.DemoExportReport());

        var seen = await session.WarningsSeenAtExportAsync();

        Assert.Equal(WarningsSeen.Of("Priya", _clock.Now, review.Warnings), seen);
        Assert.Empty(session.WarningsBeforeExport());
        Assert.NotNull(session.DemoExportReport());
        // Kept with the save's approval: whoever opens it next is not asked again.
        var reopened = (await DraftSession.OpenAsync(Store, session.Id!.Value))!;
        Assert.Empty(reopened.WarningsBeforeExport());
        Assert.NotNull(reopened.DemoExportReport());
        Assert.Equal(seen, reopened.CurrentApproval!.WarningsSeen);
    }

    // Once per version: going on again changes nothing and records nothing more.
    [Fact]
    public async Task Going_on_twice_is_recorded_once()
    {
        var session = await Approved();
        var first = await session.WarningsSeenAtExportAsync();
        var events = Records.Activity.Events.Count;
        _clock.Now += TimeSpan.FromMinutes(5);

        Assert.Equal(first, await session.WarningsSeenAtExportAsync());
        Assert.Equal(events, Records.Activity.Events.Count);
        Assert.Equal(first.At, Assert.Single(Records.Approvals.Rows).WarningsSeenAt);
    }

    // A new save is a new version: its warnings are shown again once it is approved.
    [Fact]
    public async Task A_new_save_shows_the_warnings_again()
    {
        var session = await Approved();
        await session.WarningsSeenAtExportAsync();

        session.Editor.Subject.Text = "WE’RE TURNING TWO!";
        await session.SaveAsync();
        Assert.Null(session.CurrentApproval);
        Assert.Empty(session.WarningsBeforeExport());
        Assert.Null(session.DemoExportReport());

        await session.ApproveAsync("Priya");
        Assert.NotEmpty(session.WarningsBeforeExport());
        Assert.Null(session.DemoExportReport());
        // And as whoever opens it next reads it from the table.
        var reopened = (await DraftSession.OpenAsync(Store, session.Id!.Value))!;
        Assert.NotEmpty(reopened.WarningsBeforeExport());
        Assert.Null(reopened.CurrentApproval!.WarningsSeen);
    }

    // Her label is not the email: a save that changes only the label carries the approval, and with
    // it that the warnings were seen.
    [Fact]
    public async Task A_label_only_save_keeps_it()
    {
        var session = await Approved();
        var seen = await session.WarningsSeenAtExportAsync();
        _clock.Now += TimeSpan.FromMinutes(5);

        session.Editor.Label = "Beauty Bank -- first send";
        await session.SaveAsync();

        Assert.Equal(seen, session.CurrentApproval!.WarningsSeen);
        Assert.Empty(session.WarningsBeforeExport());
        Assert.NotNull(session.DemoExportReport());
        Assert.Empty((await DraftSession.OpenAsync(Store, session.Id!.Value))!.WarningsBeforeExport());
    }

    // Withdrawing the approval, or undoing the save, leaves it behind with the approval; approved
    // again, the warnings are shown again.
    [Fact]
    public async Task A_withdrawn_approval_takes_it_with_it()
    {
        var session = await Approved();
        await session.WarningsSeenAtExportAsync();

        await session.WithdrawApprovalAsync();
        Assert.Empty(session.WarningsBeforeExport());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WarningsSeenAtExportAsync());

        await session.ApproveAsync("Priya");
        Assert.NotEmpty(session.WarningsBeforeExport());
        Assert.NotEmpty((await DraftSession.OpenAsync(Store, session.Id!.Value))!.WarningsBeforeExport());
    }

    // Only an approved version as it stands: not one with an edit since, and not one never approved.
    [Fact]
    public async Task There_is_nothing_to_go_on_past_without_the_approval_in_force()
    {
        var session = await Approved();
        session.Editor.Subject.Text = "WE’RE TURNING TWO!";

        Assert.Empty(session.WarningsBeforeExport());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WarningsSeenAtExportAsync());
        Assert.Null(Assert.Single(Records.Approvals.Rows).WarningsSeenBy);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Store.WarningsSeenAtExportAsync(session.Latest! with { SavedAt = session.Latest!.SavedAt.AddDays(1) }, []));
    }

    // ---- Warnings that come later (owner, 2026-10-09) ----

    // Her known tiers say Platinum Member is $249: one warning the version did not have when she went on.
    private static readonly BusinessContext WithKnownTiers = new("Neelam Aesthetics")
    {
        Known = new KnownItems(
        [
            new KnownTier(KnownItem.NewId(), "Gold Member", 149m, []),
            new KnownTier(KnownItem.NewId(), "Platinum Member", 249m, []),
        ]),
    };

    [Fact]
    public async Task A_warning_that_appears_after_she_went_on_brings_the_list_back_with_only_it()
    {
        var session = await Approved();
        var before = session.WarningsBeforeExport();
        await session.WarningsSeenAtExportAsync();
        _clock.Now += TimeSpan.FromMinutes(5);

        var later = session.WarningsBeforeExport(business: WithKnownTiers);

        var only = Assert.Single(later);
        Assert.Equal("'Platinum Member' is $249/month in your known items; here it is $299/month.", only.Message);
        Assert.DoesNotContain(later, w => before.Contains(w));
        Assert.Null(session.DemoExportReport(business: WithKnownTiers));
        // Going on past it: the export is there, the record says who and when anew, and both sets count.
        var seen = await session.WarningsSeenAtExportAsync(business: WithKnownTiers);
        Assert.Equal(_clock.Now, seen.At);
        Assert.Empty(session.WarningsBeforeExport(business: WithKnownTiers));
        Assert.NotNull(session.DemoExportReport(business: WithKnownTiers));
        Assert.Equal(2, Records.Activity.Events.Count(e => e.Action == ActivityAction.WarningsSeenAtExport));
        Assert.Empty((await DraftSession.OpenAsync(Store, session.Id!.Value))!.WarningsBeforeExport(business: WithKnownTiers));
    }

    // A warning she was shown stays shown while it says the same; one that went away needs nothing.
    [Fact]
    public async Task A_warning_already_seen_does_not_come_back_and_one_that_went_away_needs_nothing()
    {
        var session = await Approved();
        await session.WarningsSeenAtExportAsync(business: WithKnownTiers);

        // The known-tier warning gone (her known items as before): nothing to show, the export is there.
        Assert.Empty(session.WarningsBeforeExport());
        Assert.NotNull(session.DemoExportReport());
        // And back again, as she was shown it: still nothing.
        Assert.Empty(session.WarningsBeforeExport(business: WithKnownTiers));
        Assert.NotNull(session.DemoExportReport(business: WithKnownTiers));
    }

    // What she was shown stays shown: going on again later, past something else, does not forget it.
    [Fact]
    public async Task A_warning_shown_once_stays_shown_after_a_later_export()
    {
        var session = await Approved();
        await session.WarningsSeenAtExportAsync(business: WithKnownTiers);
        var repriced = WithKnownTiers with
        {
            Known = new KnownItems(
            [
                new KnownTier(KnownItem.NewId(), "Gold Member", 149m, []),
                new KnownTier(KnownItem.NewId(), "Platinum Member", 199m, []),
            ]),
        };
        Assert.Single(session.WarningsBeforeExport(business: repriced));
        await session.WarningsSeenAtExportAsync(business: repriced);

        Assert.Empty(session.WarningsBeforeExport(business: WithKnownTiers));
    }

    // A row from before keys were kept says who and when but not which: every warning is shown once
    // more, and going on then records the keys, so it is not shown again.
    [Fact]
    public async Task A_row_from_before_keys_were_kept_shows_the_list_once_more()
    {
        var session = await Approved();
        var row = Assert.Single(Records.Approvals.Rows);
        await Records.Approvals.PutAsync(row with { WarningsSeenBy = "Priya", WarningsSeenAt = _clock.Now });
        var reopened = (await DraftSession.OpenAsync(Store, session.Id!.Value))!;
        Assert.NotNull(reopened.CurrentApproval!.WarningsSeen);
        Assert.Null(reopened.CurrentApproval!.WarningsSeen!.Keys);

        var shown = reopened.WarningsBeforeExport();
        Assert.Equal(CampaignGate.DemoReview(reopened.Editor.Status().Review!.Campaign, reopened.CurrentApproval!).Warnings, shown);
        Assert.Null(reopened.DemoExportReport());

        await reopened.WarningsSeenAtExportAsync();
        Assert.Empty(reopened.WarningsBeforeExport());
        Assert.Empty((await DraftSession.OpenAsync(Store, session.Id!.Value))!.WarningsBeforeExport());
    }

    // ---- Who, when, and nothing else ----

    // In Prototype nobody signs in: it goes against the name typed for that version's approval, as
    // the approval does. Signed in, it is the signed-in user, whatever name was typed.
    [Fact]
    public async Task Who_is_the_typed_approver_in_prototype_and_the_sign_in_when_enforced()
    {
        var demo = await Approved();
        await demo.WarningsSeenAtExportAsync();
        var signedIn = await Approved(StoreFor(Asha));
        await signedIn.WarningsSeenAtExportAsync();

        Assert.Equal("Priya", demo.CurrentApproval!.WarningsSeen!.By);
        Assert.Equal("Asha Patel", signedIn.CurrentApproval!.WarningsSeen!.By);
        Assert.Equal(
            [("Priya", ActivityAction.WarningsSeenAtExport), ("Asha Patel", ActivityAction.WarningsSeenAtExport)],
            Records.Activity.Events.Where(e => e.Action == ActivityAction.WarningsSeenAtExport).Select(e => (e.Actor, e.Action)));
    }

    // The row is the approval's, with a name and a time added; the event names the campaign and the
    // save by id. Neither holds which finding, what it said, or anything of the campaign.
    [Fact]
    public async Task The_row_and_the_event_hold_no_content()
    {
        var session = await Approved();
        var warnings = session.WarningsBeforeExport();
        await session.WarningsSeenAtExportAsync();

        var row = TableMetadata.FromApproval(Assert.Single(Records.Approvals.Rows));
        Assert.Equal(
            ["ApprovedAt", "ApprovedBy", "PartitionKey", "RowKey", "WarningsSeen", "WarningsSeenAt", "WarningsSeenBy", "Withdrawn"],
            row.Keys.Where(k => k is not ("odata.etag" or "Timestamp")).Order(StringComparer.Ordinal));
        Assert.Equal("Priya", row["WarningsSeenBy"]);
        Assert.Equal(_clock.Now, row["WarningsSeenAt"]);
        // Which warnings, as hashes only: one 32-hex key per warning shown, and nothing else.
        var keys = ((string)row["WarningsSeen"]).Split(',');
        Assert.Equal(warnings.Select(w => w.SeenKey).Order(StringComparer.Ordinal), keys);
        Assert.All(keys, k => Assert.Matches("^[0-9a-f]{32}$", k));
        var seen = Assert.Single(Records.Activity.Events, e => e.Action == ActivityAction.WarningsSeenAtExport);
        Assert.Equal((session.Id!.Value.ToString("N"), session.Latest!.BlobName.Split('/')[2][..^".json".Length]),
            (seen.EntityId, seen.SaveStamp));
        var stored = string.Join("\n", row.Concat(TableMetadata.FromActivity(seen, Guid.NewGuid()))
            .Select(p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.All(warnings, w =>
        {
            Assert.DoesNotContain(w.Message, stored);
            Assert.DoesNotContain(w.Rule, stored);
            Assert.DoesNotContain(w.Location, stored);
        });
        Assert.DoesNotContain("Beauty Bank", stored);
    }

    // ---- Where each finding's field is ----

    [Theory]
    [InlineData("Offer › Tier 2 › Benefit", 2)]
    [InlineData("Offer 2 › Name", 3)]
    [InlineData("Opening, paragraph 2", 1)]
    [InlineData("Headline", 0)]
    [InlineData("Subject", null)]
    [InlineData("Whole email", null)]
    [InlineData("Openings", null)]
    public void A_finding_names_the_block_its_field_is_in(string location, int? expected) =>
        Assert.Equal(expected, FindingPlace.BlockIndex(location, ["Headline", "Opening", "Offer", "Offer 2"]));
}

/// <summary>
/// The list on the campaign page, before the export: shown in place of the export, every warning
/// with a link to its field, and one button to go on. The page tests read the page as it is first
/// sent; no test drives the button's click (no Blazor circuit), so going on is done through the
/// store, as the button's handler does through the session.
/// </summary>
public class ExportWarningsPageTests(DemoApp app) : IClassFixture<DemoApp>
{
    private CampaignStore Store => app.Stores.Campaigns(DemoApp.Client, Actor.Demo);

    private async Task<string> Get(string path)
    {
        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private async Task<SaveRef> Approved(Guid? id = null, CampaignDraft? draft = null)
    {
        var save = await Store.SaveDraftAsync(id ?? Guid.NewGuid(), "WE’RE TURNING ONE!", draft ?? DraftFixtures.Finished());
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return await Store.ApproveAsync(save, "Priya");
    }

    // "Export anyway", as its handler does through the session: every warning the page lists, shown.
    private Task<SaveRef> GoneOnPast(SaveRef save, CampaignDraft? draft = null) =>
        Store.WarningsSeenAtExportAsync(save, SeenAtExport.KeysOf(draft ?? DraftFixtures.Finished()));

    private static string Panel(string page) =>
        Regex.Match(page, "<section class=\"card export-warnings\"(.*?)</section>", RegexOptions.Singleline).Value;

    [Fact]
    public async Task Every_warning_is_listed_with_a_way_to_its_field_and_no_export_until_she_goes_on()
    {
        var save = await Approved();

        var page = await Get($"/campaigns/{save.Id}");

        var panel = Panel(page);
        Assert.Contains("Before you copy it into Square", panel);
        Assert.Contains("None of them stops the email.", panel);
        Assert.Contains(">Export anyway</button>", panel);
        // Exactly the warnings the gate has, each once, and none of them asked to be fixed first.
        var warnings = CampaignGate.DemoReview(DraftFixtures.Finished().Build().Campaign!, save.Approval!).Warnings.ToList();
        Assert.NotEmpty(warnings);
        Assert.Equal(warnings.Count, Regex.Matches(panel, "<li class=\"finding warning\">").Count);
        var labels = CampaignEditor.Open(DraftFixtures.Finished()).Blocks.Select(b => b.Label).ToList();
        foreach (var warning in warnings)
        {
            Assert.Contains(warning.Message, panel);
            var block = FindingPlace.BlockIndex(warning.Location, labels);
            if (block is { } i)
            {
                Assert.Contains($"<a class=\"muted\" href=\"campaigns/{save.Id}#block-{i}\">{warning.Location}</a>", panel);
                // And the field is there to land on.
                Assert.Contains($"id=\"block-{i}\"", page);
            }
        }
        Assert.Contains("#block-", panel);
        // No export of either kind beside it.
        Assert.DoesNotContain("<h2>Copy into Square</h2>", page);
        Assert.DoesNotContain("data-copy", page);
        Assert.DoesNotContain("Download for an assistant", page);
    }

    [Fact]
    public async Task Once_she_goes_on_the_list_is_not_shown_again_for_that_version()
    {
        var save = await GoneOnPast(await Approved());

        var page = await Get($"/campaigns/{save.Id}");

        Assert.Empty(Panel(page));
        Assert.DoesNotContain("Export anyway", page);
        Assert.Contains("<h2>Copy into Square</h2>", page);
        Assert.Contains("Download for an assistant (JSON)", page);
    }

    [Fact]
    public async Task A_new_save_once_approved_shows_the_list_again()
    {
        var first = await GoneOnPast(await Approved());
        var edited = DraftFixtures.Finished();
        edited.Subject.Set("WE’RE TURNING TWO!");

        await Approved(first.Id, edited);
        var page = await Get($"/campaigns/{first.Id}");

        Assert.Contains("Export anyway", Panel(page));
        Assert.DoesNotContain("data-copy", page);
    }

    // Gone on past all but one, as when that one appeared later: the list comes back with it alone,
    // and says it is new since she last exported.
    [Fact]
    public async Task A_warning_new_since_the_last_export_is_listed_alone_and_called_new()
    {
        var save = await Approved();
        var warnings = CampaignGate.DemoReview(DraftFixtures.Finished().Build().Campaign!, save.Approval!).Warnings.ToList();
        Assert.True(warnings.Count > 1);
        var later = warnings[^1];
        await Store.WarningsSeenAtExportAsync(save, warnings[..^1].Select(w => w.SeenKey).ToList());

        var panel = Panel(await Get($"/campaigns/{save.Id}"));

        Assert.Contains("1 new thing is worth a look since you last exported. None of them stops the email.", panel);
        Assert.Single(Regex.Matches(panel, "<li class=\"finding warning\">"));
        Assert.Contains(later.Message, panel);
        Assert.All(warnings[..^1], w => Assert.DoesNotContain(w.Message, panel));
        Assert.Contains(">Export anyway</button>", panel);
    }

    [Fact]
    public async Task A_label_only_save_keeps_the_export()
    {
        var first = await GoneOnPast(await Approved());
        var session = (await DraftSession.OpenAsync(Store, first.Id))!;
        session.Editor.Label = "Beauty Bank -- first send";
        app.Clock.Now += TimeSpan.FromSeconds(1);
        await session.SaveAsync();

        var page = await Get($"/campaigns/{first.Id}");

        Assert.Empty(Panel(page));
        Assert.Contains("<h2>Copy into Square</h2>", page);
    }
}
