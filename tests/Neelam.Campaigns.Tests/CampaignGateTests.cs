using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

public class CampaignGateTests
{
    private static readonly Finding Typo = new(
        Severity.Blocker, "ai-spelling", "Opening ¶1", "\"couldn’t\" is fine; \"woudl\" is not.", "woudl");

    [Fact]
    public async Task Corrected_email_with_a_clean_proofread_can_export()
    {
        var report = await CampaignGate.ReviewAsync(SampleCampaigns.Corrected(), FakeProofreader.Clean);

        Assert.True(report.CanExport, string.Join("\n", report.Blockers.Select(b => b.Message)));
    }

    [Fact]
    public async Task Ai_error_blocks_export()
    {
        var report = await CampaignGate.ReviewAsync(SampleCampaigns.Corrected(), new FakeProofreader(Typo));

        Assert.False(report.CanExport);
        Assert.Contains(report.Blockers, f => f.Rule == "ai-spelling");
    }

    [Fact]
    public async Task Dismissed_ai_error_becomes_a_warning_naming_who_dismissed_it()
    {
        var dismissal = new Dismissal("ai-spelling", "woudl", "Intentional, it's a pun.", "Neelam");

        var report = await CampaignGate.ReviewAsync(
            SampleCampaigns.Corrected(), new FakeProofreader(Typo), [dismissal]);

        Assert.True(report.CanExport);
        var warning = Assert.Single(report.Warnings, f => f.Rule == "ai-spelling");
        Assert.Contains("Dismissed by Neelam", warning.Message);
    }

    [Fact]
    public async Task Rule_blockers_cannot_be_dismissed()
    {
        var dismissal = new Dismissal("tier-names-unique", "", "Looks fine to me.", "Someone");

        var report = await CampaignGate.ReviewAsync(
            SampleCampaigns.SecondSend(), FakeProofreader.Clean, [dismissal]);

        Assert.Contains(report.Blockers, f => f.Rule == "tier-names-unique");
    }

    [Fact]
    public async Task Proofread_outage_fails_closed()
    {
        var down = new FakeProofreader { Throws = new HttpRequestException("503 Service Unavailable") };

        var report = await CampaignGate.ReviewAsync(SampleCampaigns.Corrected(), down);

        Assert.False(report.CanExport);
        Assert.Contains(report.Blockers, f => f.Rule == "ai-unavailable");
    }

    [Fact]
    public async Task Proofread_outage_can_be_dismissed_by_a_named_person()
    {
        var down = new FakeProofreader { Throws = new HttpRequestException("503 Service Unavailable") };
        var dismissal = new Dismissal("ai-unavailable", "", "Read it twice myself.", "Neelam");

        var report = await CampaignGate.ReviewAsync(SampleCampaigns.Corrected(), down, [dismissal]);

        Assert.True(report.CanExport);
    }
}
