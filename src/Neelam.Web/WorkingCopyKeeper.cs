using Neelam.Campaigns;
using Neelam.Campaigns.Storage;

namespace Neelam.Web;

/// <summary>
/// One open campaign page's working copy (owner, 2026-11-03): a recovery copy of what the form holds and has
/// not saved, written <see cref="WorkingCopyOptions.Debounce"/> after she stops changing it, and at least every
/// <see cref="WorkingCopyOptions.Cap"/> while she goes on (<see cref="WorkingCopies"/>). Never written when it
/// would hold what it already holds, or the newest save; never for a campaign with no save, which has no
/// address she could open it again at. Disposed with the page, so its timer stops.
/// </summary>
/// <remarks>
/// <para>
/// The page looks after each render (<see cref="Looked"/>): every change she makes, typed into the page's own
/// fields or told by the fields it holds (<see cref="FieldsComponent"/>), ends in a render of the page, so a
/// change is told apart by the form's content, not by which field it came through. A render with nothing
/// changed moves nothing.
/// </para>
/// <para>
/// Each page remembers the version of its own last write and writes with If-Match. When another tab wrote
/// since, the page says so (<see cref="OtherTab"/>) and writes anyway: the last writer wins. Two tabs are
/// not handled beyond that (concurrency phase 3, not built).
/// </para>
/// <para>
/// A write that fails never reaches her: it is logged, and tried again at the next change or after
/// <see cref="WorkingCopyOptions.Cap"/>. The page's own actions pause it (<see cref="PauseAsync"/>), so a
/// write never lands across a save; the page removes the copy once a save goes through, and whenever it
/// replaces the form from storage (<see cref="RemoveAsync"/>).
/// </para>
/// Everything but the timer's callback runs on the page's circuit, one thing at a time, so it needs no lock.
/// </remarks>
/// <param name="onCircuit">Runs work on the page's circuit and then renders the page (the page's own InvokeAsync).</param>
public sealed class WorkingCopyKeeper(
    WorkingCopies copies, TimeProvider clock, WorkingCopyOptions options, Func<Func<Task>, Task> onCircuit, ILogger log) : IDisposable
{
    private DraftSession? _session;

    // The form at its last look, to tell a change; null with nothing to keep.
    private string? _seen;

    // What the working copy holds as far as this page knows; null for none.
    private string? _written;

    // What this page's next write must find there: its own last write, none, or anything when it cannot know.
    private BlobExpected _expected = BlobExpected.Anything;

    // The first change not written yet: the cap runs from it.
    private DateTimeOffset? _since;

    private ITimer? _timer;
    private Task _writing = Task.CompletedTask;
    private int _paused;
    private int _disposed;

    /// <summary>True once another tab has written this campaign's working copy since this page last did.</summary>
    public bool OtherTab { get; private set; }

    /// <summary>
    /// Keeps <paramref name="session"/>'s form from now: the working copy as just read (<paramref name="copy"/>),
    /// or none; <paramref name="known"/> false when it could not be read, so the next write takes whatever is there.
    /// </summary>
    public void Track(DraftSession? session, WorkingCopy? copy, bool known = true)
    {
        _session = session;
        _seen = Form();
        _written = copy?.Json;
        _expected = copy is not null ? BlobExpected.Version(copy.ETag) : known ? BlobExpected.Nothing : BlobExpected.Anything;
        _since = null;
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // The form's content, while there is a saved campaign to keep it for.
    private string? Form() =>
        _session is { Id: not null, Latest: not null } session ? CampaignJson.SerializeDraft(session.Editor.Draft) : null;

    /// <summary>
    /// After each render of the page: when the form has changed since the last look, the write is set for
    /// <see cref="WorkingCopyOptions.Debounce"/> from now, or <see cref="WorkingCopyOptions.Cap"/> from the first
    /// change not written yet, whichever comes first.
    /// </summary>
    public void Looked()
    {
        if (IsDisposed || _paused > 0) return;
        var form = Form();
        if (form is null || form == _seen) return;
        _seen = form;
        var now = clock.GetUtcNow();
        _since ??= now;
        var due = now + options.Debounce < _since.Value + options.Cap ? now + options.Debounce : _since.Value + options.Cap;
        Schedule(due - now);
    }

    private void Schedule(TimeSpan due)
    {
        if (IsDisposed) return;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        if (_timer is null) _timer = clock.CreateTimer(_ => Due(), null, due, Timeout.InfiniteTimeSpan);
        else _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    // From the timer, on its own thread: the write happens on the page's circuit.
    private void Due()
    {
        if (IsDisposed) return;
        _ = OnCircuitAsync();
    }

    private async Task OnCircuitAsync()
    {
        try
        {
            await onCircuit(() => _writing = WriteAsync());
        }
        catch (Exception ex) when (IsDisposed || ex is ObjectDisposedException or OperationCanceledException)
        {
            // The page closed meanwhile.
        }
        catch (Exception ex)
        {
            // Never the page's end: a working copy is a courtesy, and she is shown nothing.
            log.LogError(ex, "Could not keep the working copy of an open campaign.");
        }
    }

    private async Task WriteAsync()
    {
        if (IsDisposed || _paused > 0 || _session is not { Id: { } id, Latest: { } latest } session) return;
        _since = null;
        // Taken whole now, before anything is awaited.
        var draft = session.Editor.Draft;
        var json = CampaignJson.SerializeDraft(draft);
        if (json == _written || session.IsNewestSave(json)) return;
        try
        {
            var etag = await copies.WriteAsync(id, draft, latest.SavedAt, _expected);
            if (etag is null)
            {
                // Another tab wrote or removed it since: said, and then the last writer wins.
                OtherTab = true;
                etag = await copies.WriteAsync(id, draft, latest.SavedAt, BlobExpected.Anything);
            }
            _expected = etag is null ? BlobExpected.Anything : BlobExpected.Version(etag);
            _written = json;
        }
        catch (Exception ex) when (StorageFailed.Is(ex))
        {
            log.LogWarning(ex, "Could not write the working copy of campaign {Campaign}; it is tried again.", id);
            _since ??= clock.GetUtcNow();
            Schedule(options.Cap);
        }
    }

    /// <summary>One of the page's own actions starts: nothing is written until it is done, and a write under way finishes first.</summary>
    public async Task PauseAsync()
    {
        _paused++;
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            await _writing;
        }
        catch (Exception)
        {
            // Logged where it ran (OnCircuitAsync); the action goes on.
        }
    }

    /// <summary>The page's own action is done: the form is kept from here, as <paramref name="session"/> now holds it.</summary>
    public void Resume(DraftSession? session)
    {
        if (_paused > 0) _paused--;
        _session = session;
        // A change during the action, or one the action made, is seen at once.
        _seen = null;
        Looked();
    }

    /// <summary>
    /// The page saved, or replaced its form from storage: the working copy goes. Storage failing is logged and
    /// leaves it to the sweep, or to the page's next write over it.
    /// </summary>
    public async Task RemoveAsync(Guid campaignId)
    {
        try
        {
            await DiscardAsync(campaignId);
        }
        catch (Exception ex) when (StorageFailed.Is(ex))
        {
            log.LogWarning(ex, "Could not remove the working copy of campaign {Campaign}; the sweep will.", campaignId);
            _expected = BlobExpected.Anything;
        }
    }

    /// <summary>"Discard": the working copy removed, as she asked. Storage failing throws, so the page can say it did not happen.</summary>
    public async Task DiscardAsync(Guid campaignId)
    {
        await copies.RemoveAsync(campaignId);
        _written = null;
        _expected = BlobExpected.Nothing;
        OtherTab = false;
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
    }
}
