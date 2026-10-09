using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

public class EditorExportTests
{
    // Proofread, nothing blocking, and what is worth a look shown at export and gone on past.
    private static async Task<ReviewReport> Passed() =>
        await CampaignGate.ReviewAsync(SampleCampaigns.Corrected(), FakeProofreader.Clean,
            warningsSeen: new WarningsSeen("Neelam", new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)));

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

    // The template's marker, 🤍 for Neelam, starts each line, as in the real sends.
    [Fact]
    public async Task Benefits_are_worded_by_the_model_not_typed_by_hand()
    {
        var text = EditorExport.PlainText(await Passed());

        Assert.Contains("🤍 50% off one wellness injection per visit", text);
        Assert.Contains("🤍 1 complimentary wellness injection per visit", text);
        Assert.Contains("🤍 50% off any additional wellness injections per visit", text);
        Assert.Contains("🤍 $25 birthday credit during your birth month", text);
        Assert.DoesNotContain("Complimentary Wellness Injections", text);
    }

    [Fact]
    public async Task Each_tier_is_its_name_then_its_price_then_its_benefits()
    {
        var text = EditorExport.PlainText(await Passed());

        Assert.Contains("Gold Member:\n🤍 $149/month\n🤍 $25 birthday credit", text);
        Assert.Contains("Platinum Member:\n🤍 $299/month\n🤍 $75 birthday credit", text);
    }

    // The paste sequence is the real email's Square layout: header, spacer, headline, one text
    // block for the greeting and opening, the offer's heading, one text block from the offer's
    // details through the sign-off, the photo, the button.
    [Fact]
    public async Task Export_matches_the_real_emails_square_layout()
    {
        var kinds = EditorExport.Blocks(await Passed()).Select(b => b.Kind);

        Assert.Equal(
            [BlockKind.Header, BlockKind.Spacer, BlockKind.Heading, BlockKind.Text, BlockKind.Heading,
             BlockKind.Text, BlockKind.Image, BlockKind.Button],
            kinds);
    }

    [Fact]
    public async Task Photos_are_named_for_the_person_placing_them()
    {
        var blocks = EditorExport.Blocks(await Passed());

        Assert.Equal("Principals toasting", blocks[0].Image!.Name);
        Assert.Equal("Principals seated", Assert.Single(blocks, b => b.Kind == BlockKind.Image).Image!.Name);
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
