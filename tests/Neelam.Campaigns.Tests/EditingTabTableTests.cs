using Azure.Data.Tables;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The editing tabs table's row mapping (static, offline) and the in-memory table that stands in for it:
/// ids and times only, one client's rows never another's.
/// </summary>
public class EditingTabTableTests
{
    private static readonly ClientName SalonOne = new("test-salon-one");
    private static readonly ClientName SalonTwo = new("test-salon-two");
    private static readonly Guid CampaignId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid TabA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TabB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static (InMemoryTabRows Rows, ManualClock Clock) Table()
    {
        var clock = new ManualClock(Start);
        return (new InMemoryTabRows(clock), clock);
    }

    [Fact]
    public void A_campaign_row_is_keyed_by_client_kind_and_id_and_its_tab()
    {
        var row = EditingTabTable.FromTab(SalonOne, TabSubject.Campaign(CampaignId), TabA, null);
        Assert.Equal("test-salon-one|campaign|11111111222233334444555555555555", row.PartitionKey);
        Assert.Equal("aaaaaaaa000000000000000000000001", row.RowKey);
    }

    [Fact]
    public void A_template_row_is_keyed_the_same_way_with_its_own_kind()
    {
        var row = EditingTabTable.FromTab(SalonOne, TabSubject.Template(CampaignId), TabA, null);
        Assert.Equal("test-salon-one|template|11111111222233334444555555555555", row.PartitionKey);
    }

    [Fact]
    public void The_look_has_no_id_in_its_key()
    {
        var row = EditingTabTable.FromTab(SalonOne, TabSubject.Look, TabA, null);
        Assert.Equal("test-salon-one|look", row.PartitionKey);
    }

    [Fact]
    public void No_key_holds_a_character_a_table_refuses()
    {
        foreach (var subject in new[] { TabSubject.Campaign(CampaignId), TabSubject.Template(CampaignId), TabSubject.Look })
        {
            var row = EditingTabTable.FromTab(SalonOne, subject, TabA, Start);
            foreach (var key in new[] { row.PartitionKey, row.RowKey })
                Assert.DoesNotContain(key, c => c is '/' or '\\' or '#' or '?' || char.IsControl(c));
        }
    }

    [Fact]
    public void Only_the_last_typed_time_is_kept_and_only_when_there_is_one()
    {
        var typed = EditingTabTable.FromTab(SalonOne, TabSubject.Look, TabA, Start);
        Assert.Equal(Start, typed.GetDateTimeOffset("LastTyped"));
        // Beside the keys and the service's own columns, nothing: no content.
        Assert.Equal(["LastTyped", "PartitionKey", "RowKey"],
            typed.Keys.Where(k => k != "Timestamp" && k != "odata.etag").Order());

        var quiet = EditingTabTable.FromTab(SalonOne, TabSubject.Look, TabA, null);
        Assert.False(quiet.ContainsKey("LastTyped"));
    }

    [Fact]
    public void A_row_reads_back_as_the_tab_with_the_services_timestamp()
    {
        var row = EditingTabTable.FromTab(SalonOne, TabSubject.Campaign(CampaignId), TabA, Start);
        row.Timestamp = Start.AddSeconds(30);
        var tab = EditingTabTable.ToTab(SalonOne, TabSubject.Campaign(CampaignId), row);
        Assert.Equal(new TabRow(TabA, Start, Start.AddSeconds(30)), tab);

        row.Remove("LastTyped");
        Assert.Null(EditingTabTable.ToTab(SalonOne, TabSubject.Campaign(CampaignId), row).LastTyped);
    }

    [Fact]
    public void A_row_of_another_client_or_subject_is_refused_not_shown()
    {
        var row = EditingTabTable.FromTab(SalonTwo, TabSubject.Campaign(CampaignId), TabA, null);
        row.Timestamp = Start;
        Assert.Throws<InvalidDataException>(() => EditingTabTable.ToTab(SalonOne, TabSubject.Campaign(CampaignId), row));
        Assert.Throws<InvalidDataException>(() => EditingTabTable.ToTab(SalonTwo, TabSubject.Template(CampaignId), row));
    }

    [Fact]
    public void A_row_with_no_timestamp_is_refused()
    {
        var row = EditingTabTable.FromTab(SalonOne, TabSubject.Look, TabA, null);
        Assert.Throws<InvalidDataException>(() => EditingTabTable.ToTab(SalonOne, TabSubject.Look, row));
    }

    [Fact]
    public void A_query_names_one_partition_and_a_sweep_one_time()
    {
        Assert.Equal("PartitionKey eq 'test-salon-one|look'", EditingTabTable.PartitionFilter(SalonOne, TabSubject.Look));
        Assert.Equal("Timestamp lt datetime'2026-11-05T12:00:00Z'", EditingTabTable.OlderThanFilter(Start));
    }

    [Fact]
    public async Task A_write_is_listed_with_the_clocks_time_as_its_timestamp()
    {
        var (rows, clock) = Table();
        await rows.WriteAsync(SalonOne, TabSubject.Campaign(CampaignId), TabA, Start.AddSeconds(-5));
        var listed = await rows.ListAsync(SalonOne, TabSubject.Campaign(CampaignId));
        Assert.Equal([new TabRow(TabA, Start.AddSeconds(-5), Start)], listed);

        clock.Now = Start.AddMinutes(1);
        await rows.WriteAsync(SalonOne, TabSubject.Campaign(CampaignId), TabA, null);
        Assert.Equal([new TabRow(TabA, null, Start.AddMinutes(1))], await rows.ListAsync(SalonOne, TabSubject.Campaign(CampaignId)));
    }

    [Fact]
    public async Task A_list_holds_that_subject_of_that_client_only()
    {
        var (rows, _) = Table();
        var campaign = TabSubject.Campaign(CampaignId);
        await rows.WriteAsync(SalonOne, campaign, TabA, null);
        await rows.WriteAsync(SalonOne, campaign, TabB, null);
        await rows.WriteAsync(SalonTwo, campaign, Guid.NewGuid(), null);
        await rows.WriteAsync(SalonOne, TabSubject.Template(CampaignId), Guid.NewGuid(), null);
        await rows.WriteAsync(SalonOne, TabSubject.Campaign(Guid.NewGuid()), Guid.NewGuid(), null);
        await rows.WriteAsync(SalonOne, TabSubject.Look, Guid.NewGuid(), null);

        Assert.Equal([TabA, TabB], (await rows.ListAsync(SalonOne, campaign)).Select(t => t.TabId).Order());
        Assert.Single(await rows.ListAsync(SalonOne, TabSubject.Look));
        Assert.Empty(await rows.ListAsync(SalonTwo, TabSubject.Look));
    }

    [Fact]
    public async Task A_delete_removes_that_tabs_row_and_a_missing_row_is_no_error()
    {
        var (rows, _) = Table();
        await rows.WriteAsync(SalonOne, TabSubject.Look, TabA, null);
        await rows.WriteAsync(SalonOne, TabSubject.Look, TabB, null);

        await rows.DeleteAsync(SalonOne, TabSubject.Look, TabA);
        await rows.DeleteAsync(SalonOne, TabSubject.Look, TabA);
        await rows.DeleteAsync(SalonTwo, TabSubject.Look, TabB);

        Assert.Equal([TabB], (await rows.ListAsync(SalonOne, TabSubject.Look)).Select(t => t.TabId));
    }

    [Fact]
    public async Task A_sweep_removes_rows_older_than_the_time_given_across_clients_and_counts_them()
    {
        var (rows, clock) = Table();
        await rows.WriteAsync(SalonOne, TabSubject.Look, TabA, null);
        await rows.WriteAsync(SalonTwo, TabSubject.Campaign(CampaignId), TabB, null);
        clock.Now = Start.AddHours(23);
        await rows.WriteAsync(SalonOne, TabSubject.Look, Guid.NewGuid(), null);

        Assert.Equal(2, await rows.SweepAsync(Start.AddHours(12)));
        Assert.Single(rows.Rows);
        Assert.Equal(0, await rows.SweepAsync(Start.AddHours(12)));
        // A row exactly as old as the limit is not older than it.
        Assert.Equal(0, await rows.SweepAsync(Start.AddHours(23)));
    }

    [Fact]
    public async Task A_sweep_keeps_a_row_written_again_after_it_picked_it_as_the_services_delete_by_etag_does()
    {
        // The table tells an unchanged row by reference: two rows alike in every value are still two rows.
        Assert.False(EqualityComparer<TableEntity>.Default.Equals(new TableEntity("p", "r"), new TableEntity("p", "r")));

        var (rows, clock) = Table();
        await rows.WriteAsync(SalonOne, TabSubject.Look, TabA, null);
        await rows.WriteAsync(SalonOne, TabSubject.Look, TabB, null);
        clock.Now = Start.AddHours(23);
        rows.BeforeSweepRemoves = () => rows.WriteAsync(SalonOne, TabSubject.Look, TabA, null);

        Assert.Equal(1, await rows.SweepAsync(Start.AddHours(12)));
        Assert.Equal([new TabRow(TabA, null, Start.AddHours(23))], await rows.ListAsync(SalonOne, TabSubject.Look));
    }
}
