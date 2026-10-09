using System.Text.Json.Nodes;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// A campaign's label: the client's own name for it, such as "Beauty Bank -- first send", so two
/// campaigns with the same subject can be told apart in her list. Saved in the draft's JSON, and
/// never part of the email, its checks or its export, nor in a blob's name or metadata.
/// </summary>
public class CampaignLabelTests
{
    // Words the default policy warns about, so a label that leaked into the checks would show.
    private const string Label = "Beauty Bank savings account -- first send (label only)";
    private const string Marker = "label only";

    private static readonly Approval ByPriya = new("Priya", new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    private CampaignStore Store => new(_container, _clock, Grace);

    private static CampaignDraft Labelled(CampaignDraft draft, string? label = Label)
    {
        draft.Label = label;
        return draft;
    }

    private static List<(string, Severity, string, string)> Findings(IEnumerable<Finding> findings) =>
        findings.Select(f => (f.Rule, f.Severity, f.Location, f.Message)).ToList();

    [Fact]
    public void A_label_round_trips_in_the_draft_s_json_trimmed()
    {
        var json = CampaignJson.SerializeDraft(Labelled(DraftFixtures.Finished(), $"  {Label} "));

        var reopened = CampaignJson.DeserializeDraft(json);

        Assert.Equal(Label, reopened.Label);
        Assert.Equal(Label, (string?)JsonNode.Parse(json)!["label"]);
    }

    // A blank label is no label, and is saved as drafts were before labels existed: no "label" at all.
    [Fact]
    public void A_draft_without_a_label_saves_as_before()
    {
        var json = CampaignJson.SerializeDraft(Labelled(DraftFixtures.Finished(), "   "));

        Assert.False(JsonNode.Parse(json)!.AsObject().ContainsKey("label"));
        Assert.Equal(CampaignJson.SerializeDraft(DraftFixtures.Finished()), json);
        Assert.Null(CampaignJson.DeserializeDraft(json).Label);
    }

    // Every draft saved before 2026-10-09 has no "label"; it still opens, unlabelled and otherwise whole.
    [Fact]
    public void An_old_draft_without_a_label_still_reads()
    {
        var saved = JsonNode.Parse(CampaignJson.SerializeDraft(Labelled(DraftFixtures.Finished())))!.AsObject();
        Assert.True(saved.Remove("label"));

        var old = CampaignJson.DeserializeDraft(saved.ToJsonString());

        Assert.Null(old.Label);
        Assert.Equal(
            EditorExport.Preview(DraftFixtures.Finished().Build().Campaign!),
            EditorExport.Preview(old.Build().Campaign!));
    }

    [Fact]
    public void The_label_is_never_in_the_export_or_the_rule_findings()
    {
        var plain = DraftFixtures.Finished().Build().Campaign!;
        var labelled = Labelled(DraftFixtures.Finished()).Build().Campaign!;

        // The finished email: the same campaign, the same findings, the same export, with or without it.
        Assert.Equal(plain.Subject, labelled.Subject);
        Assert.Equal(plain.Preheader, labelled.Preheader);
        Assert.Equal(Findings(CampaignReview.Check(plain).Findings), Findings(CampaignReview.Check(labelled).Findings));
        var report = CampaignGate.DemoReview(labelled, ByPriya);
        Assert.True(report.CanExport);
        Assert.Equal(EditorExport.PlainText(CampaignGate.DemoReview(plain, ByPriya)), EditorExport.PlainText(report));
        Assert.DoesNotContain(Marker, EditorExport.PlainText(report));
        Assert.DoesNotContain(EditorExport.Blocks(report), b => $"{b.Text} {b.Url} {b.Image?.Name}".Contains(Marker));
        Assert.DoesNotContain(report.Findings, f => $"{f.Location} {f.Message}".Contains(Marker));

        // An unfinished one: the checks and the preview over what is filled in ignore it too.
        var unfinished = CampaignEditor.Open(DraftFixtures.SecondSendReplayed()).Status();
        var unfinishedLabelled = CampaignEditor.Open(Labelled(DraftFixtures.SecondSendReplayed())).Status();
        Assert.Equal(Findings(unfinished.Missing), Findings(unfinishedLabelled.Missing));
        Assert.Equal(Findings(unfinished.Findings), Findings(unfinishedLabelled.Findings));
        Assert.Equal(unfinished.Preview, unfinishedLabelled.Preview);
    }

    // The owner's rule: customer data does not live in blob names or metadata. The subject's
    // "title" stays where it was; the label goes only into the JSON.
    [Fact]
    public async Task The_label_is_saved_in_the_json_and_never_in_the_blob_s_name_or_metadata()
    {
        var template = Guid.NewGuid();
        await Store.SaveTemplateAsync(template, DraftFixtures.Membership);
        _clock.Now += TimeSpan.FromMinutes(1);
        var session = (await DraftSession.StartAsync(Store, template))!;
        session.Editor.Label = Label;
        session.Editor.Subject.Text = "WE’RE TURNING ONE!";

        var save = await session.SaveAsync();

        var (_, metadata) = _container.Blobs[save.BlobName];
        Assert.Contains(Marker, _container.Text(save.BlobName));
        Assert.DoesNotContain(Marker, save.BlobName);
        Assert.DoesNotContain(metadata, m =>
            m.Key.Contains("label", StringComparison.OrdinalIgnoreCase) || Uri.UnescapeDataString(m.Value).Contains(Marker));
        Assert.Equal(Label, (await Store.LoadDraftAsync(save)).Label);
    }

    // The list reads each label from the newest save in use: not an older one, not an undone one.
    [Fact]
    public async Task The_list_reads_each_label_from_the_newest_save_in_use()
    {
        var relabelled = Guid.NewGuid();
        await Store.SaveDraftAsync(relabelled, "WE’RE TURNING ONE!", Labelled(DraftFixtures.Finished(), "First label"));
        _clock.Now += TimeSpan.FromSeconds(1);
        await Store.SaveDraftAsync(relabelled, "WE’RE TURNING ONE!", Labelled(DraftFixtures.Finished(), "Kept label"));
        _clock.Now += TimeSpan.FromSeconds(1);
        var undone = await Store.SaveDraftAsync(relabelled, "WE’RE TURNING ONE!", Labelled(DraftFixtures.Finished(), "Undone label"));
        await Store.MarkUndoneAsync(undone);
        _clock.Now += TimeSpan.FromSeconds(1);
        var unlabelled = Guid.NewGuid();
        await Store.SaveDraftAsync(unlabelled, "WE’RE TURNING ONE!", DraftFixtures.Finished());
        _container.Reads.Clear();

        var list = await DraftSession.ListLabelledAsync(Store);

        Assert.Equal([(unlabelled, null), (relabelled, "Kept label")], list.Select(c => (c.Save.Id, c.Label)));
        Assert.All(list, c => Assert.Equal("WE’RE TURNING ONE!", c.Save.Title));
        // One read per campaign listed, and never the undone save.
        Assert.Equal(2, _container.Reads.Count);
        Assert.DoesNotContain(undone.BlobName, _container.Reads);
    }
}
