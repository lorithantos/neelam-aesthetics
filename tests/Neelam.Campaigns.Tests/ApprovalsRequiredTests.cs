using System.Net;
using Azure.Data.Tables;
using Neelam.Campaigns.Storage;
using Neelam.Web.Components.Pages;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// How many people must approve a campaign version is a per-client setting (owner, 2026-11-03:
/// "Obviously at least one approver, but make the number a config setting. For now she's the only one,
/// so set hers to one."). It is registration data, an optional <c>ApprovalsRequired</c> on the client's
/// clients-table row: left out, it is one, so every client registered before needs one, as before. A
/// version counts as approved once that many different people have approved it; until then the export
/// stays shut and the Approve card says how far it has got.
/// </summary>
public class ApprovalsRequiredTests
{
    private static readonly ClientRecord Salon = new(
        new ClientName("test-salon-one"), Guid.Parse("66666666-6666-6666-6666-666666666661"), "Salon One");

    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly TestRecords _records;

    public ApprovalsRequiredTests()
    {
        this._records = new TestRecords(this._clock);
    }

    private CampaignStore Store(int approvalsRequired = ClientRecord.DefaultApprovalsRequired) =>
        this._records.Campaigns(this._container, Grace, approvalsRequired: approvalsRequired);

    // A finished campaign, saved once, open as the page would have it, over a store needing that many approvals.
    private async Task<DraftSession> SavedFinished(int approvalsRequired)
    {
        Guid id = Guid.NewGuid();
        await this.Store(approvalsRequired).SaveDraftAsync(id, "WE’RE TURNING ONE!", DraftFixtures.Finished());
        this._clock.Now += TimeSpan.FromMinutes(1);
        return (await DraftSession.OpenAsync(this.Store(approvalsRequired), id))!;
    }

    [Fact]
    public void A_registration_that_names_no_number_needs_one_and_its_row_has_no_column()
    {
        TableEntity row = TableMetadata.FromClient(Salon);

        Assert.Null(Salon.ApprovalsRequired);
        Assert.Equal(1, Salon.ApprovalsNeeded);
        Assert.False(row.ContainsKey(TableMetadata.ApprovalsRequiredColumn));
        Assert.Null(TableMetadata.ToClient(row).ApprovalsRequired);
        Assert.Equal(1, TableMetadata.ToClient(row).ApprovalsNeeded);
    }

    [Fact]
    public void The_clients_table_keeps_the_number_and_reads_it_back()
    {
        ClientRecord two = Salon with { ApprovalsRequired = 2 };

        TableEntity row = TableMetadata.FromClient(two);

        Assert.Equal(2, row.GetInt32(TableMetadata.ApprovalsRequiredColumn));
        Assert.Equal(two, TableMetadata.ToClient(row));
        Assert.Equal(2, TableMetadata.ToClient(row).ApprovalsNeeded);
        // A number typed into a table editor may be stored as Int64; it reads the same.
        row[TableMetadata.ApprovalsRequiredColumn] = 3L;
        Assert.Equal(3, TableMetadata.ToClient(row).ApprovalsRequired);
    }

    // The house style is to refuse rather than clamp: a row below one, or not a number, cannot be read,
    // and the table's rules refuse to write one, on add and on update.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_number_below_one_is_refused_where_it_is_read_and_where_it_is_written(int approvals)
    {
        TableEntity row = TableMetadata.FromClient(Salon);
        row[TableMetadata.ApprovalsRequiredColumn] = approvals;
        InvalidDataException read = Assert.Throws<InvalidDataException>(() => TableMetadata.ToClient(row));
        Assert.Contains("at least one approver", read.Message);

        InMemoryClientDirectory clients = new();
        ArgumentException added = await Assert.ThrowsAsync<ArgumentException>(
            () => clients.AddAsync(Salon with { ApprovalsRequired = approvals }));
        await clients.AddAsync(Salon);
        ArgumentException updated = await Assert.ThrowsAsync<ArgumentException>(
            () => clients.UpdateAsync(Salon with { ApprovalsRequired = approvals }));
        Assert.Equal(
            $"A campaign needs at least one approver, so the number of approvals required cannot be {approvals}.",
            added.Message);
        Assert.Equal(added.Message, updated.Message);
        Assert.Null(Assert.Single(await clients.ListAsync()).ApprovalsRequired);

        Assert.Throws<ArgumentOutOfRangeException>(() => this.Store(approvals));
    }

    [Fact]
    public void A_number_that_is_not_a_whole_number_makes_the_row_unreadable()
    {
        TableEntity row = TableMetadata.FromClient(Salon);
        row[TableMetadata.ApprovalsRequiredColumn] = "2";

        Assert.Throws<InvalidDataException>(() => TableMetadata.ToClient(row));
    }

    // The Clients page has no field for it yet, and the table's update replaces the whole row, so an
    // edit there carries the number through rather than setting the client back to one.
    [Fact]
    public void An_edit_on_the_clients_page_keeps_the_number()
    {
        ClientForm form = ClientForm.Of(Salon with { ApprovalsRequired = 2 });
        form.DisplayName = "Salon One, renamed";

        ClientRecord? edited = form.ToRecord().Client;

        Assert.Equal(2, edited!.ApprovalsRequired);
        Assert.Null(ClientForm.Of(Salon).ToRecord().Client!.ApprovalsRequired);
        Assert.Null(new ClientForm { Name = "new-salon", GroupId = Guid.NewGuid().ToString(), DisplayName = "New" }
            .ToRecord().Client!.ApprovalsRequired);
    }

    // With one approval required, which every client has unless its row says otherwise, the approvals
    // table's rows are written exactly as before: through approving, going on past the warnings at
    // export, a label-only save carrying the approval, and a withdrawal, no row gains a column.
    [Fact]
    public async Task With_one_approval_required_every_row_is_written_as_before()
    {
        DraftSession session = await this.SavedFinished(ClientRecord.DefaultApprovalsRequired);
        DateTimeOffset approvedAt = this._clock.Now;
        await session.ApproveAsync("Priya");
        this._clock.Now += TimeSpan.FromMinutes(1);
        await session.WarningsSeenAtExportAsync();
        session.Editor.Label = "November send";
        await session.SaveAsync();
        this._clock.Now += TimeSpan.FromMinutes(1);
        await session.WithdrawApprovalAsync();

        List<ApprovalRecord> rows = this._records.Approvals.Rows.OrderBy(r => r.Stamp, StringComparer.Ordinal).ToList();
        Assert.Equal(2, rows.Count);
        foreach (ApprovalRecord row in rows)
        {
            Assert.Null(row.MoreApprovers);
            Assert.Null(row.Approval.Others);
            TableEntity entity = TableMetadata.FromApproval(row);
            Assert.Equal("Priya", entity.GetString("ApprovedBy"));
            Assert.Equal(approvedAt, entity.GetDateTimeOffset("ApprovedAt"));
        }
        // The columns each row had before approvals could need more than one person, and no others.
        string[] before = ["ApprovedBy", "ApprovedAt", "Withdrawn", "WarningsSeenBy", "WarningsSeenAt", "WarningsSeen"];
        Assert.Equal(before.Order(StringComparer.Ordinal), Columns(rows[0]));
        Assert.Equal(before.Append("WithdrawnAt").Order(StringComparer.Ordinal), Columns(rows[1]));
    }

    // A row's own columns as the table would store them, keys aside, in order.
    private static IEnumerable<string> Columns(ApprovalRecord row) =>
        TableMetadata.FromApproval(row).Keys.Except(["PartitionKey", "RowKey"]).Order(StringComparer.Ordinal).ToList();

    // The heart of it: with two required, one approval is not enough. The version is approved in part,
    // the same person cannot approve it twice, it cannot be exported, and a second person approves it.
    [Fact]
    public async Task With_two_required_a_version_is_approved_once_two_different_people_have()
    {
        DraftSession session = await this.SavedFinished(approvalsRequired: 2);

        Approval first = await session.ApproveAsync("Priya");

        Assert.Equal("Priya", first.Names);
        Assert.Null(session.CurrentApproval);
        Assert.Null(session.Latest!.Approval);
        Assert.Equal("Priya", session.CurrentPartApproval!.By);
        Assert.Null(session.DemoExportReport());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WarningsSeenAtExportAsync());
        Assert.Equal("Approved by 1 of 2: Priya.", DraftSession.PartApprovalWords(session.CurrentPartApproval, session.ApprovalsRequired));
        Assert.Equal(
            "It needs approving by one more person before it can be copied into Square.",
            DraftSession.StillNeededWords(session.CurrentPartApproval, session.ApprovalsRequired));
        // Reopened, it reads the same: approved in part.
        DraftSession reopened = (await DraftSession.OpenAsync(this.Store(2), session.Id!.Value))!;
        Assert.Null(reopened.CurrentApproval);
        Assert.Equal("Priya", reopened.CurrentPartApproval!.Names);

        // Different people: the same name, however typed, is the same person.
        InvalidOperationException twice = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApproveAsync(" priya "));
        Assert.Equal("priya has already approved this version. It needs approving by someone else too.", twice.Message);

        this._clock.Now += TimeSpan.FromMinutes(3);
        Approval both = await session.ApproveAsync("Sam");

        Assert.Equal("Priya and Sam", both.Names);
        Assert.Equal(this._clock.Now, both.LastAt);
        Assert.Equal(both, session.CurrentApproval);
        Assert.Null(session.CurrentPartApproval);
        await session.WarningsSeenAtExportAsync();
        Assert.NotNull(session.DemoExportReport());
        ApprovalRecord row = Assert.Single(this._records.Approvals.Rows);
        Assert.Equal("Priya", row.ApprovedBy);
        Assert.Equal([new Approver("Sam", this._clock.Now)], row.MoreApprovers!);
        // One person's approval of the very same row, read by a store needing one, is enough there:
        // what decides is the number the store is given, the client's.
        Assert.NotNull((await DraftSession.OpenAsync(this.Store(1), session.Id.Value))!.CurrentApproval);
    }

    // Decision 9: a version approved by fewer people than needed is not approved yet, so on opening it
    // takes her treatments' current prices, as any unapproved campaign does.
    [Fact]
    public async Task A_version_approved_in_part_follows_her_list_on_opening()
    {
        CampaignEditor editor = PromotionTests.FillerPromo(template: KnownPromotionTests.Editable);
        Assert.True(editor.UsePromotion(editor.Blocks.OfType<PromotionBlockEditor>().Single(), KnownPromotionTests.Known.Promotions[0],
            KnownPromotionTests.Known, replaceFinePrint: true).Used);
        Guid id = Guid.NewGuid();
        await this.Store(2).SaveDraftAsync(id, "Filler Promo", editor.Draft);
        this._clock.Now += TimeSpan.FromMinutes(1);
        await (await DraftSession.OpenAsync(this.Store(2), id))!.ApproveAsync("Priya");

        DraftSession reopened = (await DraftSession.OpenAsync(this.Store(2), id))!;

        Assert.NotNull(reopened.CurrentPartApproval);
        Assert.Equal(
            ["From your treatments: Full-Face Facial Balancing is now regularly $5,400 (was $5,100)."],
            reopened.FollowHerList(new KnownItems([KnownPromotionTests.Midface, KnownPromotionTests.Balancing with { RegularPrice = 5400m }])));
    }

    // The assistant export names everyone who approved and when the last did; with one, as before.
    [Fact]
    public async Task The_export_names_every_approver()
    {
        DraftSession session = await this.SavedFinished(approvalsRequired: 2);
        await session.ApproveAsync("Priya");
        this._clock.Now += TimeSpan.FromMinutes(3);
        await session.ApproveAsync("Sam");
        await session.WarningsSeenAtExportAsync();

        AssistantExportDocument document = AssistantExport.Build(session.DemoExportReport()!);

        Assert.Equal(new ExportedApproval("Priya and Sam", this._clock.Now), document.Review.Approval);
    }

    // Withdraw approval takes back the approvals so far; approving again starts afresh.
    [Fact]
    public async Task Approvals_so_far_can_be_withdrawn_and_start_again()
    {
        DraftSession session = await this.SavedFinished(approvalsRequired: 2);
        await session.ApproveAsync("Priya");
        this._clock.Now += TimeSpan.FromMinutes(1);

        await session.WithdrawApprovalAsync();

        Assert.Null(session.CurrentPartApproval);
        Assert.Equal(this._clock.Now, Assert.Single(this._records.Approvals.Rows).WithdrawnAt);
        await session.ApproveAsync("Sam");
        Assert.Equal("Sam", session.CurrentPartApproval!.Names);
        Assert.Null(Assert.Single(this._records.Approvals.Rows).MoreApprovers);
    }

    // A label-only save carries the approvals so far, as it carries a whole approval.
    [Fact]
    public async Task A_label_only_save_carries_the_approvals_so_far()
    {
        DraftSession session = await this.SavedFinished(approvalsRequired: 2);
        await session.ApproveAsync("Priya");
        this._clock.Now += TimeSpan.FromMinutes(1);

        session.Editor.Label = "November send";
        await session.SaveAsync();

        Assert.Equal("Priya", session.CurrentPartApproval!.Names);
        await session.ApproveAsync("Sam");
        Assert.Equal("Priya and Sam", session.CurrentApproval!.Names);
    }

    // Another tab's approval is a change the page has not seen: approving from the page that has not
    // seen it is refused, and records nothing.
    [Fact]
    public async Task An_approval_given_in_another_tab_meanwhile_refuses_this_one()
    {
        DraftSession mine = await this.SavedFinished(approvalsRequired: 3);
        DraftSession early = (await DraftSession.OpenAsync(this.Store(3), mine.Id!.Value))!;
        await mine.ApproveAsync("Priya");
        DraftSession theirs = (await DraftSession.OpenAsync(this.Store(3), mine.Id.Value))!;
        this._clock.Now += TimeSpan.FromMinutes(1);
        await theirs.ApproveAsync("Sam");

        // A page that saw Priya's approval and not Sam's, and one that saw neither, are both out of date.
        Assert.Equal(CampaignChange.Approved, mine.ChangeSince(await mine.NewestAsync()));
        CampaignChangedException refused = await Assert.ThrowsAsync<CampaignChangedException>(() => mine.ApproveAsync("Lee"));
        Assert.Equal(CampaignChange.Approved, refused.Change);
        await Assert.ThrowsAsync<CampaignChangedException>(() => early.ApproveAsync("Lee"));
        Assert.Equal(["Sam"], Assert.Single(this._records.Approvals.Rows).MoreApprovers!.Select(a => a.By));
        // Withdrawn in another tab, the approvals so far are gone from what this page holds.
        await theirs.WithdrawApprovalAsync();
        Assert.Equal(CampaignChange.ApprovalWithdrawn, mine.ChangeSince(await mine.NewestAsync()));
    }

    // Rows written with more approvers keep every column an older build reads, meaning what it did:
    // the first approver and when. The later ones are one optional column, read back in order.
    [Fact]
    public void A_row_with_more_approvers_keeps_the_old_columns_and_round_trips()
    {
        DateTimeOffset at = this._clock.Now;
        ApprovalRecord record = new(
            new ClientName("test-salon-one"), Guid.NewGuid(), "20261103T120000.0000000Z", "Priya", at,
            MoreApprovers: [new Approver("Sam", at.AddMinutes(2)), new Approver("Lee", at.AddMinutes(5))]);

        TableEntity row = TableMetadata.FromApproval(record);

        Assert.Equal("Priya", row.GetString("ApprovedBy"));
        Assert.Equal(at, row.GetDateTimeOffset("ApprovedAt"));
        Assert.False(row.GetBoolean("Withdrawn"));
        ApprovalRecord back = TableMetadata.ToApproval(row);
        Assert.Equal(record.MoreApprovers, back.MoreApprovers!);
        Assert.Equal("Priya, Sam and Lee", back.Approval.Names);
        Assert.Equal(3, back.ApproverCount);
        // A row from before has no such column, and reads with no more approvers.
        row.Remove(TableMetadata.MoreApproversColumn);
        Assert.Null(TableMetadata.ToApproval(row).MoreApprovers);
    }
}

/// <summary>
/// The campaign page, on the demo, for a client whose registration needs two approvals: it shows how
/// far the approval has got and keeps the export shut until the second person approves.
/// </summary>
public class ApprovalsRequiredPageTests(DemoApp app) : IClassFixture<DemoApp>
{
    private async Task<string> Get(string path)
    {
        if ((await app.Clients.ListAsync()).Count == 0)
        {
            await app.Clients.AddAsync(new ClientRecord(DemoApp.Client, Guid.NewGuid(), "Neelam Aesthetics") { ApprovalsRequired = 2 });
        }
        HttpResponseMessage response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task One_approval_of_two_shows_progress_and_no_export_and_the_second_opens_it()
    {
        CampaignStore store = app.Stores.Campaigns(DemoApp.Client, Actor.Demo, approvalsRequired: 2);
        CampaignDraft draft = DraftFixtures.Finished();
        SaveRef saved = await store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", draft);
        app.Clock.Now += TimeSpan.FromSeconds(1);
        SaveRef part = await store.ApproveAsync(saved, "Priya");

        string page = await this.Get($"/campaigns/{saved.Id}");

        Assert.Equal("Approved by 1 of 2: Priya.", RenderedPage.Named(page, "approval-progress"));
        Assert.Equal(
            "It needs approving by one more person before it can be copied into Square.",
            RenderedPage.Named(page, "approval-still-needed"));
        Assert.Equal(0, RenderedPage.Count(page, "approved"));
        Assert.Equal(1, RenderedPage.Count(page, "approve-button"));
        Assert.Equal(0, RenderedPage.Count(page, "export-not-checked"));

        await store.WarningsSeenAtExportAsync(await store.ApproveAsync(part, "Sam"), SeenAtExport.KeysOf(draft));
        page = await this.Get($"/campaigns/{saved.Id}");

        Assert.Matches(@"^Approved by Priya and Sam, 3 Oct 2026, 5:0\d AM PDT\.$", RenderedPage.Named(page, "approved"));
        Assert.Equal(0, RenderedPage.Count(page, "approval-progress"));
        Assert.Equal(1, RenderedPage.Count(page, "export-not-checked"));
    }
}
