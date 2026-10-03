using Neelam.Campaigns;
using Neelam.Campaigns.Claude;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The clients table's description of a business guides the AI proofread: it travels from the
/// client record through the gate into Claude's request, as marked-off background.
/// </summary>
public class BusinessContextTests
{
    private static readonly ClientRecord Neelam = new(
        new ClientName("neelam-aesthetics"), Guid.NewGuid(), "Neelam Aesthetics",
        "A small, family-run medical aesthetics clinic offering injectables and skin treatments.");

    [Fact]
    public async Task The_gate_hands_the_business_to_the_proofreader()
    {
        var proofreader = new FakeProofreader();

        await CampaignGate.ReviewAsync(SampleCampaigns.Corrected(), proofreader, business: Neelam.Business);

        Assert.Equal(new BusinessContext("Neelam Aesthetics", Neelam.Description), proofreader.LastBusiness);
    }

    [Fact]
    public void Claude_is_told_about_the_business_inside_marked_tags()
    {
        var campaign = SampleCampaigns.Corrected();

        var prompt = ClaudeProofreader.UserPrompt(campaign, EditorExport.Preview(campaign), Neelam.Business);

        var start = prompt.IndexOf("<business>", StringComparison.Ordinal);
        var end = prompt.IndexOf("</business>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var inside = prompt[start..end];
        Assert.Contains("Name: Neelam Aesthetics", inside);
        Assert.Contains("family-run medical aesthetics clinic", inside);
    }

    [Fact]
    public void With_no_business_known_nothing_is_said_about_one()
    {
        var campaign = SampleCampaigns.Corrected();

        Assert.DoesNotContain("<business>", ClaudeProofreader.UserPrompt(campaign, EditorExport.Preview(campaign), null));
    }

    [Fact]
    public void The_clients_table_keeps_the_description()
    {
        Assert.Equal(Neelam, TableMetadata.ToClient(TableMetadata.FromClient(Neelam)));

        var undescribed = Neelam with { Description = null };
        var row = TableMetadata.FromClient(undescribed);
        Assert.False(row.ContainsKey("Description"));
        Assert.Null(TableMetadata.ToClient(row).Description);
    }
}
