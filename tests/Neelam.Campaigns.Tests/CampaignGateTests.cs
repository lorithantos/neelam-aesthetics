using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

public class CampaignGateTests
{
    private static readonly Finding Typo = new(
        Severity.Blocker, "ai-spelling", "Opening ¶1", "\"couldn’t\" is fine; \"woudl\" is not.", "woudl");

    // Shown the "Worth a look" findings at export, and went on: the corrected email keeps "Beauty
    // Bank" on purpose, so there is always something worth a look in it.
    private static readonly WarningsSeen Seen = new("Neelam", new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Corrected_email_with_a_clean_proofread_can_export()
    {
        var report = await CampaignGate.ReviewAsync(SampleCampaigns.Corrected(), FakeProofreader.Clean, warningsSeen: Seen);

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
            SampleCampaigns.Corrected(), new FakeProofreader(Typo), [dismissal], warningsSeen: Seen);

        Assert.True(report.CanExport);
        var warning = Assert.Single(report.Warnings, f => f.Rule == "ai-spelling");
        Assert.Contains("Dismissed by Neelam", warning.Message);
    }

    [Fact]
    public async Task Rule_blockers_cannot_be_dismissed()
    {
        var dismissal = new Dismissal("terms-required", "", "Looks fine to me.", "Someone");

        var report = await CampaignGate.ReviewAsync(
            SampleCampaigns.SecondSend(), FakeProofreader.Clean, [dismissal]);

        Assert.Contains(report.Blockers, f => f.Rule == "terms-required");
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

        var report = await CampaignGate.ReviewAsync(SampleCampaigns.Corrected(), down, [dismissal], warningsSeen: Seen);

        Assert.True(report.CanExport);
    }

    // Enforced, as production runs (owner, 2026-10-09): proofread, nothing blocking, and still no
    // export until a person has been shown what is worth a look and gone on. Both forms of the
    // export refuse it from the same report, with the same reason; once seen, both are there.
    [Fact]
    public async Task Enforced_export_waits_until_the_warnings_are_seen()
    {
        var unseen = await CampaignGate.ReviewAsync(SampleCampaigns.Corrected(), FakeProofreader.Clean);

        Assert.Empty(unseen.Blockers);
        Assert.NotEmpty(unseen.Warnings);
        Assert.True(unseen.WarningsToSee);
        Assert.False(unseen.CanExport);
        var copy = Assert.Throws<CampaignBlockedException>(() => EditorExport.Blocks(unseen));
        var json = Assert.Throws<CampaignBlockedException>(() => AssistantExport.Build(unseen, new Approval("Neelam", Seen.At)));
        Assert.Equal(copy.Message, json.Message);
        Assert.Contains("worth a look that nobody has been shown at export yet", copy.Message);

        var seen = await CampaignGate.ReviewAsync(SampleCampaigns.Corrected(), FakeProofreader.Clean, warningsSeen: Seen);
        Assert.False(seen.WarningsToSee);
        Assert.NotEmpty(EditorExport.Blocks(seen));
        Assert.NotNull(AssistantExport.Build(seen, new Approval("Neelam", Seen.At)));
    }

    // A Must fix is never passed by going on past the warnings: it still blocks, and the list of
    // warnings is not what stands in the way.
    [Fact]
    public async Task Seen_warnings_never_pass_a_must_fix()
    {
        var report = await CampaignGate.ReviewAsync(SampleCampaigns.SecondSend(), FakeProofreader.Clean, warningsSeen: Seen);

        Assert.NotEmpty(report.Blockers);
        Assert.False(report.WarningsToSee);
        Assert.False(report.CanExport);
        Assert.Contains("blocking problem", Assert.Throws<CampaignBlockedException>(() => EditorExport.Blocks(report)).Message);
    }

    // With nothing worth a look there is nothing to be shown, and nothing to wait for.
    [Fact]
    public async Task With_nothing_worth_a_look_nothing_waits()
    {
        var report = await CampaignGate.ReviewAsync(new Campaign("Hello", [new HeadingBlock("Headline", "Hello")]), FakeProofreader.Clean);

        Assert.Empty(report.Findings);
        Assert.False(report.WarningsToSee);
        Assert.True(report.CanExport);
    }
}
