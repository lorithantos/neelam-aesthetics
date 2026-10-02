using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

public class EditorExportTests
{
    private static async Task<ReviewReport> Passed() =>
        await CampaignGate.ReviewAsync(SampleCampaigns.Corrected(), FakeProofreader.Clean);

    [Fact]
    public async Task Blocked_email_has_no_export()
    {
        var report = await CampaignGate.ReviewAsync(SampleCampaigns.SecondSend(), FakeProofreader.Clean);

        var ex = Assert.Throws<CampaignBlockedException>(() => EditorExport.Blocks(report));
        Assert.False(ex.Report.CanExport);
    }

    [Fact]
    public void Unproofread_email_has_no_export()
    {
        var rulesOnly = CampaignReview.Check(SampleCampaigns.Corrected());

        var ex = Assert.Throws<CampaignBlockedException>(() => EditorExport.Blocks(rulesOnly));
        Assert.Contains("not been through the full review", ex.Message);
    }

    [Fact]
    public async Task Benefits_are_worded_by_the_model_not_typed_by_hand()
    {
        var text = EditorExport.PlainText(await Passed());

        Assert.Contains("• 50% off one wellness injection per visit", text);
        Assert.Contains("• 1 complimentary wellness injection per visit", text);
        Assert.Contains("• 50% off any additional wellness injections per visit", text);
        Assert.Contains("• $25 birthday credit during your birth month", text);
        Assert.DoesNotContain("Complimentary Wellness Injections", text);
    }

    [Fact]
    public async Task Tiers_are_headed_by_distinct_name_and_price()
    {
        var headings = EditorExport.Blocks(await Passed())
            .Where(b => b.Kind == BlockKind.Heading)
            .Select(b => b.Text)
            .ToList();

        Assert.Contains("Gold Member — $149/month", headings);
        Assert.Contains("Platinum Member — $299/month", headings);
    }

    [Fact]
    public async Task Export_ends_the_offer_with_a_button()
    {
        var blocks = EditorExport.Blocks(await Passed());

        var button = Assert.Single(blocks, b => b.Kind == BlockKind.Button);
        Assert.Equal("Join the Beauty Bank", button.Text);
        Assert.Equal("https", button.Url!.Scheme);
    }
}
