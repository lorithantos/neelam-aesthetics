using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

public class CampaignJsonTests
{
    [Fact]
    public void Unreviewed_copy_is_still_unreviewed_after_save_and_reopen()
    {
        var draft = DraftFixtures.SecondSendReplayed();
        var before = draft.Build().Problems.Select(p => (p.Rule, p.Location, p.Message)).ToList();

        var reopened = CampaignJson.DeserializeDraft(CampaignJson.SerializeDraft(draft));

        Assert.Equal(before, reopened.Build().Problems.Select(p => (p.Rule, p.Location, p.Message)).ToList());
        Assert.Equal("Tier 1", reopened.Offer("Offer").Tiers[1].Benefits[2].CopiedFrom);
    }

    [Fact]
    public void Finished_draft_reopens_to_the_same_email()
    {
        var draft = DraftFixtures.Finished();

        var reopened = CampaignJson.DeserializeDraft(CampaignJson.SerializeDraft(draft));

        Assert.Equal(
            EditorExport.Preview(draft.Build().Campaign!),
            EditorExport.Preview(reopened.Build().Campaign!));
    }

    [Fact]
    public void Origins_are_kept_not_flattened()
    {
        var reopened = CampaignJson.DeserializeDraft(CampaignJson.SerializeDraft(DraftFixtures.Finished()));

        Assert.Equal(Origin.Template, reopened.SignOff("Sign-off").Origin);
        Assert.Equal(Origin.Entered, reopened.Text("Headline").Origin);
        Assert.Equal(Origin.Empty, reopened.Preheader.Origin);
        Assert.Equal(DraftFixtures.Membership.Blocks.Select(b => b.Label), reopened.Blocks.Select(b => b.Label));
        Assert.True(reopened.Offer("Offer").IsRecurring);
        Assert.Equal("🤍", ((OfferBlockDraft)reopened["Offer"]).Marker);
        Assert.Equal(Origin.Template, reopened.Header("Header").Origin);
        Assert.Equal("Principals seated", reopened.Image("Photo").Value.Name);
    }

    // A template is data: its blocks, their order, what is fixed and what is required all survive.
    [Fact]
    public void Template_round_trips()
    {
        var json = CampaignJson.SerializeTemplate(DraftFixtures.Membership);

        var t = CampaignJson.DeserializeTemplate(json);

        Assert.Equal(DraftFixtures.Membership.Name, t.Name);
        Assert.Equal(DraftFixtures.Membership.Blocks.Select(b => (b.Label, b.Type, b.Required, b.Recurring)),
            t.Blocks.Select(b => (b.Label, b.Type, b.Required, b.Recurring)));
        // The fixed content too: the same campaign written through either template reads the same.
        Assert.Equal(
            EditorExport.Preview(DraftFixtures.Finished(DraftFixtures.Membership).Build().Campaign!),
            EditorExport.Preview(DraftFixtures.Finished(t).Build().Campaign!));
    }

    [Fact]
    public void Unknown_schema_version_is_refused_rather_than_misread()
    {
        var json = CampaignJson.SerializeDraft(DraftFixtures.Finished())
            .Replace($"\"schema\": {CampaignJson.SchemaVersion}", "\"schema\": 99");

        Assert.Throws<InvalidDataException>(() => CampaignJson.DeserializeDraft(json));
    }
}
