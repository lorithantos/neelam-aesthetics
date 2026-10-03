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
