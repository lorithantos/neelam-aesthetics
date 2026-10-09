using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>The mapping between records and table rows; no table is reached.</summary>
public class TableMetadataTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly ClientName Neelam = new("neelam-aesthetics");
    private static readonly Caller NeelamMember = new("neelam-owner", IsOperator: false, [Neelam]);

    [Fact]
    public void A_client_row_round_trips()
    {
        var client = new ClientRecord(Neelam, Guid.NewGuid(), "Neelam Aesthetics");

        var row = TableMetadata.FromClient(client);

        Assert.Equal("neelam-aesthetics", row.RowKey);
        Assert.Equal(client, TableMetadata.ToClient(row));
    }

    // Keyed by client (partition), campaign and save stamp (row); the withdrawal kept with its time.
    [Fact]
    public void An_approval_row_round_trips_keyed_by_client_campaign_and_save()
    {
        var campaign = Guid.Parse("6b1f0c1e-9a35-4c2e-8e57-0d3c9c1a2b44");
        var standing = new ApprovalRecord(Neelam, campaign, "20261009T120000.0000000Z", "Priya", Now);
        var withdrawn = standing with { WithdrawnAt = Now.AddMinutes(5) };

        var row = TableMetadata.FromApproval(standing);

        Assert.Equal(("neelam-aesthetics", "6b1f0c1e9a354c2e8e570d3c9c1a2b44_20261009T120000.0000000Z"), (row.PartitionKey, row.RowKey));
        Assert.Equal(false, row["Withdrawn"]);
        Assert.False(row.ContainsKey("WithdrawnAt"));
        Assert.Equal(standing, TableMetadata.ToApproval(row));
        Assert.Equal(true, TableMetadata.FromApproval(withdrawn)["Withdrawn"]);
        Assert.Equal(withdrawn, TableMetadata.ToApproval(TableMetadata.FromApproval(withdrawn)));
        // Who went on past the warnings at export, and when: left out until then, kept once there.
        Assert.False(row.ContainsKey("WarningsSeenBy"));
        Assert.Null(TableMetadata.ToApproval(row).Approval.WarningsSeen);
        var seen = standing with { WarningsSeenBy = "Priya", WarningsSeenAt = Now.AddMinutes(2) };
        var seenRow = TableMetadata.FromApproval(seen);
        Assert.Equal(("Priya", Now.AddMinutes(2)), (seenRow["WarningsSeenBy"], seenRow["WarningsSeenAt"]));
        Assert.Equal(seen, TableMetadata.ToApproval(seenRow));
        Assert.Equal(new WarningsSeen("Priya", Now.AddMinutes(2)), TableMetadata.ToApproval(seenRow).Approval.WarningsSeen);
    }

    // Withdrawn with no time, as something outside the app might leave it, is still withdrawn.
    [Fact]
    public void An_approval_marked_withdrawn_without_a_time_is_withdrawn()
    {
        var row = TableMetadata.FromApproval(new ApprovalRecord(Neelam, Guid.NewGuid(), "20261009T120000.0000000Z", "Priya", Now));
        row["Withdrawn"] = true;

        Assert.True(TableMetadata.ToApproval(row).Withdrawn);
    }

    // The query names the client's own partition and only this campaign's rows.
    [Fact]
    public void The_approvals_query_stays_in_the_client_s_partition()
    {
        var campaign = Guid.Parse("6b1f0c1e-9a35-4c2e-8e57-0d3c9c1a2b44");

        Assert.Equal(
            "PartitionKey eq 'neelam-aesthetics' and RowKey ge '6b1f0c1e9a354c2e8e570d3c9c1a2b44_' and RowKey lt '6b1f0c1e9a354c2e8e570d3c9c1a2b44`'",
            TableMetadata.ApprovalsFilter(Neelam, campaign));
    }

    [Fact]
    public void An_activity_row_round_trips_in_the_client_s_partition()
    {
        var saved = new ActivityEvent(Neelam, ActivityEntity.Campaign, "6b1f0c1e9a354c2e8e570d3c9c1a2b44",
            "20261009T120000.0000000Z", ActivityAction.DeletedBySweep, "undo sweep", Now);
        var registered = saved with
        {
            Entity = ActivityEntity.ClientRegistration, EntityId = "neelam-aesthetics", SaveStamp = null,
            Action = ActivityAction.ClientRegistered,
        };

        var row = TableMetadata.FromActivity(saved, Guid.Empty);

        Assert.Equal("neelam-aesthetics", row.PartitionKey);
        Assert.StartsWith("20261003T120000.0000000Z-", row.RowKey);
        Assert.Equal(("Campaign", "DeletedBySweep"), (row["Entity"], row["Action"]));
        Assert.Equal(saved, TableMetadata.ToActivity(row));
        Assert.False(TableMetadata.FromActivity(registered, Guid.Empty).ContainsKey("SaveStamp"));
        Assert.Equal(registered, TableMetadata.ToActivity(TableMetadata.FromActivity(registered, Guid.Empty)));
    }

    // The app's tables are the ones the Bicep creates and gives it access to.
    [Fact]
    public void The_tables_the_app_uses_are_the_ones_deployed()
    {
        var bicep = File.ReadAllText(Path.Combine(InfrastructureTests.Root, "infra", "main.bicep"));
        foreach (var table in new[]
                 {
                     TableMetadata.ClientsTable, TableMetadata.GrantsTable, TableMetadata.ApprovalsTable, TableMetadata.ActivityTable,
                 })
            Assert.Contains($"  '{table}'\n", bicep.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_client_row_with_no_group_is_refused_rather_than_read_as_no_one()
    {
        var row = TableMetadata.FromClient(new ClientRecord(Neelam, Guid.NewGuid(), "Neelam"));
        row.Remove("GroupId");

        Assert.Throws<InvalidDataException>(() => TableMetadata.ToClient(row));
    }

    [Fact]
    public void An_expiring_grant_round_trips()
    {
        var grant = SupportGrant.Give(Neelam, NeelamMember, "the export looks wrong", TimeSpan.FromHours(48), Now);

        var row = TableMetadata.FromGrant(grant, Guid.NewGuid());

        Assert.Equal("neelam-aesthetics", row.PartitionKey);
        Assert.StartsWith("20261003T120000.0000000Z-", row.RowKey);
        Assert.Equal(grant, TableMetadata.ToGrant(row));
    }

    [Fact]
    public void A_standing_grant_round_trips_with_no_expiry()
    {
        var grant = SupportGrant.Standing(Neelam, NeelamMember, "shaping the tool with Neelam until 1.0", Now);

        var row = TableMetadata.FromGrant(grant, Guid.NewGuid());

        Assert.False(row.ContainsKey("Expires"));
        Assert.Null(TableMetadata.ToGrant(row).Expires);
    }

    [Fact]
    public void Two_grants_in_the_same_instant_get_different_rows()
    {
        var grant = SupportGrant.Give(Neelam, NeelamMember, "help", TimeSpan.FromHours(1), Now);

        Assert.NotEqual(TableMetadata.FromGrant(grant, Guid.NewGuid()).RowKey, TableMetadata.FromGrant(grant, Guid.NewGuid()).RowKey);
    }
}
