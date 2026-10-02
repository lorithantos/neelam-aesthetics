using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

public class EditorExportTests
{
    [Fact]
    public void Blocked_email_has_no_export()
    {
        var ex = Assert.Throws<CampaignBlockedException>(() => EditorExport.Blocks(SampleCampaigns.AsSent()));

        Assert.False(ex.Report.CanExport);
    }

    [Fact]
    public void Benefits_are_worded_by_the_model_not_typed_by_hand()
    {
        var text = EditorExport.PlainText(SampleCampaigns.Corrected());

        Assert.Contains("• 50% off one wellness injection per visit", text);
        Assert.Contains("• 1 complimentary wellness injection per visit", text);
        Assert.Contains("• 50% off any additional wellness injections per visit", text);
        Assert.Contains("• $25 birthday credit during your birth month", text);
        Assert.DoesNotContain("Complimentary Wellness Injections", text);
    }

    [Fact]
    public void Tiers_are_headed_by_distinct_name_and_price()
    {
        var headings = EditorExport.Blocks(SampleCampaigns.Corrected())
            .Where(b => b.Kind == BlockKind.Heading)
            .Select(b => b.Text)
            .ToList();

        Assert.Contains("Gold Member — $149/month", headings);
        Assert.Contains("Platinum Member — $299/month", headings);
    }

    [Fact]
    public void Export_ends_the_offer_with_a_button()
    {
        var blocks = EditorExport.Blocks(SampleCampaigns.Corrected());

        var button = Assert.Single(blocks, b => b.Kind == BlockKind.Button);
        Assert.Equal("Join the Beauty Bank", button.Text);
        Assert.Equal("https", button.Url!.Scheme);
    }
}
