using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The owner, 2026-10-31: "the issue is that fixing ONE LLM issue clears the entire list. That means more
/// calls than we need and wastes the user's time". The LLM report is carried across edits and saves; a
/// finding greys the moment its quote leaves the form (1a) and stays in place (5a); a carried Must fix
/// still to look at stops the approval until a fresh proofread (2b); otherwise carried findings are
/// display only. Rule findings go when fixed, as before.
/// </summary>
public class LlmReportTests
{
    private readonly InMemoryBlobBackend _container = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private CampaignStore Store => Records.Campaigns(_container, TimeSpan.FromDays(1));
    private readonly DailyProofreadAllowance _allowance;

    public LlmReportTests() => _allowance = new DailyProofreadAllowance(20, _clock);

    private const string Photo = "Unique test photo";

    // In the headline, which she types for each campaign.
    private static readonly Finding Misspelt = new(Severity.Blocker, "ai-spelling", "Headline", "Misspelt.", "Recieve");
    // Found twice in the text with its spacing changed: matched on words, ignoring case.
    private static readonly Finding Spaced = new(Severity.Warning, "ai-clarity", "Headline", "Reads oddly.", "your  GLOW\ntoday");
    private static readonly Finding NoQuote = new(Severity.Warning, "ai-tone", "Whole email", "A little long.", "");
    private static readonly Finding NotInText = new(Severity.Warning, "ai-spelling", "Photo", "Typo. (Quoted from the photo: check it there.)", "Gloe");

    private async Task<DraftSession> SavedAsync(string headline = "Recieve your glow today")
    {
        var draft = DraftFixtures.Finished();
        draft.Text("Headline").Set(headline);
        draft.Image("Photo").Set(new ImageRef(Photo, "A photo"));
        var id = Guid.NewGuid();
        await Store.SaveDraftAsync(id, "Test", draft);
        _clock.Now += TimeSpan.FromMinutes(1);
        return (await DraftSession.OpenAsync(Store, id))!;
    }

    private async Task ProofreadAsync(DraftSession session, params Finding[] findings)
    {
        var proofreader = new FakeProofreader(findings) { Photos = [new PhotoCheck(Photo, "Read now.", true)] };
        Assert.True((await session.ProofreadAsync(proofreader, _allowance, _clock)).Done);
        _clock.Now += TimeSpan.FromMinutes(1);
    }

    private static TextBlockEditor Headline(DraftSession session) =>
        (TextBlockEditor)session.Editor.Blocks.Single(b => b.Label == "Headline");

    private static LlmFindingState StateOf(DraftSession session, Finding finding) =>
        session.Report!.Findings.Single(f => f.Finding == finding).State;

    [Fact]
    public async Task This_version_s_own_report_is_current_and_nothing_greys()
    {
        var session = await SavedAsync();
        await ProofreadAsync(session, Misspelt, Spaced, NoQuote, NotInText);

        var report = session.Report!;
        Assert.True(report.IsCurrent);
        Assert.Equal(session.Latest!.BlobName, report.Of.BlobName);
        Assert.All(report.Findings, f => Assert.Equal(LlmFindingState.Live, f.State));
        Assert.False(Assert.Single(report.Photos).LooksFixed);
    }

    [Fact]
    public async Task A_fixed_quote_greys_at_once_and_the_rest_stay_in_place_and_live()
    {
        var session = await SavedAsync();
        await ProofreadAsync(session, Misspelt, Spaced, NoQuote, NotInText);

        Headline(session).Field.Text = "Receive your glow today";

        var report = session.Report!;
        Assert.False(report.IsCurrent);
        Assert.Equal([Misspelt, Spaced, NoQuote, NotInText], report.Findings.Select(f => f.Finding));
        Assert.Equal(LlmFindingState.LooksFixed, StateOf(session, Misspelt));
        Assert.Equal(LlmFindingState.Live, StateOf(session, Spaced));
        // Never grey: no quote, and a quote that was not in the text (a photo's words).
        Assert.Equal(LlmFindingState.Live, StateOf(session, NoQuote));
        Assert.Equal(LlmFindingState.Live, StateOf(session, NotInText));
        Assert.Equal((3, 1), (report.StillToLookAt, report.LookFixed));
        Assert.Equal("3 still to look at, 1 looks fixed. Proofread again when you're done.", report.CarriedSummary());

        // Words only, ignoring case: respaced and in other case, the quote is still there.
        Headline(session).Field.Text = "Receive YOUR glow\n\n today";
        Assert.Equal(LlmFindingState.Live, StateOf(session, Spaced));
        Headline(session).Field.Text = "Receive your shine today";
        Assert.Equal(LlmFindingState.LooksFixed, StateOf(session, Spaced));
        Assert.Equal("2 still to look at, 2 look fixed. Proofread again when you're done.", session.Report!.CarriedSummary());

        // Put back, it is live again: measured against the form as it stands.
        Headline(session).Field.Text = "Recieve your glow today";
        Assert.Equal(LlmFindingState.Live, StateOf(session, Misspelt));
    }

    [Fact]
    public async Task The_report_carries_across_a_save_and_a_reopening_measured_against_the_version_it_read()
    {
        var session = await SavedAsync();
        await ProofreadAsync(session, Misspelt, Spaced);
        var read = session.Latest!;
        Headline(session).Field.Text = "Receive your glow today";
        await session.SaveAsync();

        Assert.Equal(read.BlobName, session.Report!.Of.BlobName);
        Assert.Equal(LlmFindingState.LooksFixed, StateOf(session, Misspelt));

        // Opened afresh: found with one listing, and that save's draft read for its words.
        var reopened = (await DraftSession.OpenAsync(Store, session.Id!.Value))!;
        Assert.Null(reopened.CurrentProofread);
        Assert.Equal((false, read.BlobName), (reopened.Report!.IsCurrent, reopened.Report.Of.BlobName));
        Assert.Equal(LlmFindingState.LooksFixed, StateOf(reopened, Misspelt));
        Assert.Equal(LlmFindingState.Live, StateOf(reopened, Spaced));
        Assert.Null(reopened.CannotProofread());

        // Undone back to the version it read: its own report again, current.
        await reopened.UndoLastSaveAsync();
        Assert.True(reopened.Report!.IsCurrent);
        Assert.NotNull(reopened.CurrentProofread);
    }

    [Fact]
    public async Task A_proofread_of_an_undone_save_is_never_carried()
    {
        var session = await SavedAsync();
        await session.SaveAsync();
        await ProofreadAsync(session, Misspelt);
        await session.UndoLastSaveAsync();

        Assert.Null(session.Report);
        Assert.Null((await DraftSession.OpenAsync(Store, session.Id!.Value))!.Report);
    }

    [Fact]
    public async Task A_photo_note_greys_only_once_that_photo_has_left_the_email()
    {
        var session = await SavedAsync();
        await ProofreadAsync(session, NoQuote);
        var photo = (ImageBlockEditor)session.Editor.Blocks.Single(b => b.Label == "Photo");

        photo.PhotoAltText = "Another description";
        Assert.False(Assert.Single(session.Report!.Photos).LooksFixed);

        photo.PhotoName = "Another photo";
        Assert.True(Assert.Single(session.Report!.Photos).LooksFixed);
    }

    // Choice 2b: a carried Must fix still to look at stops the approval until this version is
    // proofread again; fixed, or proofread afresh with nothing found, the approval goes ahead.
    [Fact]
    public async Task A_carried_must_fix_still_to_look_at_stops_the_approval_until_a_fresh_proofread()
    {
        const string stopped = "The LLM's report on an earlier version has a Must fix that doesn't look fixed yet. Fix it, or proofread this version again.";
        var session = await SavedAsync();
        await ProofreadAsync(session, Misspelt);
        session.Editor.Subject.Text += "!";
        await session.SaveAsync();

        Assert.Equal(stopped, session.CannotApprove());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApproveAsync("Priya"));

        await ProofreadAsync(session);
        Assert.Null(session.CannotApprove());
        Assert.True(session.Report!.IsCurrent);

        // Fixed instead: it looks fixed, and nothing stops the approval.
        var other = await SavedAsync();
        await ProofreadAsync(other, Misspelt);
        Headline(other).Field.Text = "Receive your glow today";
        await other.SaveAsync();
        Assert.Null(other.CannotApprove());
    }

    // Display only: a carried report is never counted as proofread, never joins the export's findings,
    // and is never recorded as seen at export.
    [Fact]
    public async Task A_carried_report_is_never_proofread_never_exported_and_never_recorded_as_seen()
    {
        var session = await SavedAsync();
        await ProofreadAsync(session, Spaced);
        Headline(session).Field.Text = "Receive your glow today";
        await session.SaveAsync();
        Assert.Equal(LlmFindingState.Live, StateOf(session, Spaced));
        await session.ApproveAsync("Priya");

        Assert.DoesNotContain(Spaced, session.WarningsBeforeExport());
        await session.WarningsSeenAtExportAsync();
        var report = session.DemoExportReport()!;
        Assert.False(report.Proofread);
        Assert.DoesNotContain(Spaced, report.Findings);
        Assert.DoesNotContain(Spaced.SeenKey, session.Latest!.Approval!.WarningsSeen?.Keys ?? new HashSet<string>());
    }
}
