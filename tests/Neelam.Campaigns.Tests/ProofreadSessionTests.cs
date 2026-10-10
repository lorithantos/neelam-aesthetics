using System.Text.Json.Nodes;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The AI proofread on the demo (owner, 2026-10-10): on her click, of a saved version; the result is
/// that version's, kept beside it in her container, and any edit voids it; its findings join the
/// rules' at the gate; a refusal or failure changes nothing else; and a day's proofreads are limited.
/// </summary>
public class ProofreadSessionTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);

    private CampaignStore Store => Records.Campaigns(_container, Grace);

    private DailyProofreadAllowance Allowance { get; }

    public ProofreadSessionTests() => Allowance = new DailyProofreadAllowance(20, _clock);

    private static readonly Finding Typo = new(Severity.Blocker, "ai-spelling", "Opening", "Misspelt. Suggest: receive", "recieve");

    private static readonly Finding Unclear = new(Severity.Warning, "ai-clarity", "Offer", "Could read two ways. Suggest: say per visit.", "per visit");

    private async Task<DraftSession> Saved(CampaignDraft? draft = null)
    {
        var id = Guid.NewGuid();
        await Store.SaveDraftAsync(id, "WE’RE TURNING ONE!", draft ?? DraftFixtures.Finished());
        _clock.Now += TimeSpan.FromMinutes(1);
        return (await DraftSession.OpenAsync(Store, id))!;
    }

    private Task<ProofreadAttempt> Proofread(DraftSession session, IProofreader proofreader) =>
        session.ProofreadAsync(proofreader, Allowance, _clock);

    // ---- The result belongs to the saved version ----

    [Fact]
    public async Task A_saved_version_is_proofread_on_asking_and_the_result_is_kept_beside_it()
    {
        var session = await Saved();
        var proofreader = new FakeProofreader(Unclear) { Photos = [new PhotoCheck("Principals toasting", "Read now.", true)] };

        var attempt = await Proofread(session, proofreader);

        Assert.True(attempt.Done);
        Assert.Equal("The AI proofread found 1 thing in this version, listed with the rules' findings.", attempt.Message);
        Assert.Single(proofreader.Read);
        var kept = Assert.Single(_container.Blobs.Keys, k => k.StartsWith("proofread/", StringComparison.Ordinal));
        Assert.Equal($"proofread/{session.Id:N}/{SaveStamp.Of(session.Latest!.SavedAt)}.json", kept);
        Assert.Equal([Unclear], session.CurrentProofread!.Findings);
        Assert.Equal("Read now.", Assert.Single(session.CurrentProofread.Photos).Note);
        Assert.Equal(_clock.GetUtcNow(), session.CurrentProofread.At);
        // Asked again, nothing runs: this version is proofread.
        Assert.Equal("This version is proofread.", (await Proofread(session, proofreader)).Message);
        Assert.Single(proofreader.Read);
    }

    [Fact]
    public async Task Opening_the_campaign_again_shows_its_saved_version_s_proofread()
    {
        var session = await Saved();
        await Proofread(session, new FakeProofreader(Unclear));

        var reopened = (await DraftSession.OpenAsync(Store, session.Id!.Value))!;

        Assert.Equal([Unclear], reopened.CurrentProofread!.Findings);
    }

    [Fact]
    public async Task Any_edit_voids_the_proofread_and_a_new_save_has_none()
    {
        var session = await Saved();
        await Proofread(session, new FakeProofreader(Unclear));

        session.Editor.Subject.Text += "!";
        Assert.Null(session.CurrentProofread);
        Assert.Equal("Changed since the last save: save it, then the proofread reads that version.", session.CannotProofread());

        await session.SaveAsync();
        Assert.Null(session.CurrentProofread);
        Assert.Null(session.CannotProofread());
    }

    // The label is never in the email, so it is never proofread.
    [Fact]
    public async Task Her_label_alone_does_not_void_it()
    {
        var session = await Saved();
        await Proofread(session, new FakeProofreader(Unclear));

        session.Editor.Label = "Beauty Bank -- first send";

        Assert.NotNull(session.CurrentProofread);
    }

    [Fact]
    public async Task Nothing_is_proofread_before_a_save()
    {
        var session = (await DraftSession.StartAsync(Store, await Template()))!;
        var proofreader = new FakeProofreader();

        var attempt = await Proofread(session, proofreader);

        Assert.False(attempt.Done);
        Assert.Equal("Save the campaign first: the AI proofread reads a saved version.", attempt.Message);
        Assert.Empty(proofreader.Read);
    }

    private async Task<Guid> Template()
    {
        var id = Guid.NewGuid();
        await Store.SaveTemplateAsync(id, DraftFixtures.Membership);
        return id;
    }

    // ---- Refused, failed or over the limit: said plainly, and the rules stand ----

    [Fact]
    public async Task A_refusal_says_so_keeps_nothing_and_leaves_the_rules_and_the_demo_export_as_they_were()
    {
        var session = await Saved();
        await session.ApproveAsync("Priya");
        var before = session.DemoExportReport();
        var refusing = new FakeProofreader { Throws = new ProofreadUnavailableException("The AI proofread declined to read this version.") };

        var attempt = await Proofread(session, refusing);

        Assert.False(attempt.Done);
        Assert.Equal("The AI proofread declined to read this version. The rules' checks above still stand.", attempt.Message);
        Assert.Null(session.CurrentProofread);
        Assert.DoesNotContain(_container.Blobs.Keys, k => k.StartsWith("proofread/", StringComparison.Ordinal));
        Assert.Equal(before?.Findings, session.DemoExportReport()?.Findings);
        Assert.Equal(before?.Proofread, session.DemoExportReport()?.Proofread);
    }

    [Fact]
    public async Task An_unexpected_failure_is_said_without_its_text()
    {
        var session = await Saved();

        var attempt = await Proofread(session, new FakeProofreader { Throws = new InvalidOperationException("internal detail") });

        Assert.Equal("The AI proofread could not run. The rules' checks above still stand.", attempt.Message);
    }

    [Fact]
    public async Task A_client_s_day_of_proofreads_is_limited_failures_included()
    {
        var allowance = new DailyProofreadAllowance(2, _clock);
        var failing = new FakeProofreader { Throws = new ProofreadUnavailableException("The AI proofread could not be reached just now.") };
        var session = await Saved();

        await session.ProofreadAsync(failing, allowance, _clock);
        await session.ProofreadAsync(failing, allowance, _clock);
        var third = await session.ProofreadAsync(new FakeProofreader(), allowance, _clock);

        Assert.False(third.Done);
        Assert.StartsWith("The AI proofread has run 2 times for you today, its daily limit.", third.Message);

        _clock.Now += TimeSpan.FromDays(1);
        Assert.True((await session.ProofreadAsync(new FakeProofreader(), allowance, _clock)).Done);
    }

    // ---- At the gate ----

    [Fact]
    public async Task Its_findings_join_the_rules_at_the_demo_export_which_then_says_proofread()
    {
        var session = await Saved();
        await session.ApproveAsync("Priya");
        await Proofread(session, new FakeProofreader(Unclear));

        // Shown at export like any warning, the AI's among them.
        Assert.Contains(Unclear, session.WarningsBeforeExport());
        await session.WarningsSeenAtExportAsync();
        var report = session.DemoExportReport()!;

        Assert.True(report.Proofread);
        Assert.Contains(Unclear, report.Findings);
        var json = JsonNode.Parse(AssistantExport.Offer(report, context: new AssistantExportContext(session.Id)).Json!)!;
        Assert.True((bool?)json["review"]!["proofread"]);
        Assert.Null(json["review"]!["notice"]);
        Assert.Contains($"{Unclear.Location}: {Unclear.Message}", json["review"]!["worthALook"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public async Task Without_a_proofread_the_demo_export_still_says_not_proofread()
    {
        var session = await Saved();
        await session.ApproveAsync("Priya");
        await session.WarningsSeenAtExportAsync();

        var report = session.DemoExportReport()!;

        Assert.False(report.Proofread);
        var json = JsonNode.Parse(AssistantExport.Offer(report).Json!)!;
        Assert.False((bool?)json["review"]!["proofread"]);
        Assert.Equal("Not proofread by AI yet", (string?)json["review"]!["notice"]);
    }

    [Fact]
    public async Task Its_must_fix_stops_the_export_and_the_approval()
    {
        var session = await Saved();
        await session.ApproveAsync("Priya");
        await session.WarningsSeenAtExportAsync();

        await Proofread(session, new FakeProofreader(Typo));

        Assert.Null(session.DemoExportReport());
        Assert.Empty(session.WarningsBeforeExport());
        Assert.Equal("Fix everything marked Must fix first.", session.CannotApprove());
    }

    // Enforced keeps its gate: no report exports without the proofread, however it failed.
    [Fact]
    public async Task In_enforced_a_failed_proofread_still_blocks_the_export()
    {
        var report = await CampaignGate.ReviewAsync(
            DraftFixtures.Finished().Build().Campaign!,
            new FakeProofreader { Throws = new ProofreadUnavailableException("The AI proofread declined to read this version.") });

        Assert.Contains(report.Blockers, f => f.Rule == "ai-unavailable");
        Assert.False(report.CanExport);
    }

    // ---- Kept, and gone with the save ----

    [Fact]
    public async Task The_trail_records_that_a_save_was_proofread_by_ids_alone()
    {
        var session = await Saved();
        await Proofread(session, new FakeProofreader(Unclear));

        var e = Assert.Single(Records.Activity.Events, e => e.Action == ActivityAction.Proofread);
        Assert.Equal(session.Id!.Value.ToString("N"), e.EntityId);
        Assert.Equal(SaveStamp.Of(session.Latest!.SavedAt), e.SaveStamp);
    }

    [Fact]
    public async Task The_sweep_deletes_a_save_s_proofread_with_it_and_keeps_her_photo_readings()
    {
        var session = await Saved();
        await Proofread(session, new FakeProofreader(Unclear));
        var readings = Records.Stores(_ => _container, Grace).PhotoReadings(TestRecords.DefaultClient);
        var hash = new string('a', 64);
        await readings.KeepAsync(hash, new PhotoReading("w", "s", "o"));
        // One left behind by a crash part-way: its save is long gone.
        _container.Put($"proofread/{Guid.NewGuid():N}/20261001T000000.0000000Z.json", "{}");

        await Store.MarkUndoneAsync(session.Latest!);
        _clock.Now += Grace + TimeSpan.FromMinutes(1);
        await Store.SweepAsync();

        Assert.Equal([$"proofread/images/{hash}.json"], _container.Blobs.Keys.Where(k => k.StartsWith("proofread/", StringComparison.Ordinal)));
        Assert.Equal(new PhotoReading("w", "s", "o"), await readings.FindAsync(hash));
    }

    [Fact]
    public async Task A_save_still_in_use_keeps_its_proofread_through_the_sweep()
    {
        var session = await Saved();
        await Proofread(session, new FakeProofreader(Unclear));

        await Store.SweepAsync();

        Assert.NotNull((await DraftSession.OpenAsync(Store, session.Id!.Value))!.CurrentProofread);
    }

    [Fact]
    public async Task Photo_readings_are_kept_by_hash_alone()
    {
        var readings = Records.Stores(_ => _container, Grace).PhotoReadings(TestRecords.DefaultClient);

        await Assert.ThrowsAsync<ArgumentException>(() => readings.KeepAsync("../drafts/x", new PhotoReading("", "", "")));
        await Assert.ThrowsAsync<ArgumentException>(() => readings.FindAsync(new string('A', 64)));
        Assert.Null(await readings.FindAsync(new string('b', 64)));
    }
}
