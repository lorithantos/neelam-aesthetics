using Neelam.Campaigns;
using Neelam.Campaigns.Storage;

namespace Neelam.Web;

/// <summary>
/// One open campaign page's working copy (owner, 2026-11-03): a recovery copy of what the form holds and has
/// not saved, written <see cref="WorkingCopyOptions.Debounce"/> after she stops changing it, and at least every
/// <see cref="WorkingCopyOptions.Cap"/> while she goes on (<see cref="WorkingCopies"/>). Never written when it
/// would hold what it already holds, or the newest save. A new campaign is kept from her first change, which
/// gives it its id and address (owner, 2026-11-03: "No typing means no harm, no foul"). Disposed with the
/// page, so its timer stops.
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
/// A write storage cannot take never reaches her: it is logged, the form goes to the server's disk instead
/// (<see cref="IWorkingCopyDisk"/>, owner, 2026-11-03), and it is tried again at the next change or after
/// <see cref="WorkingCopyOptions.Cap"/>; the first write storage takes again replaces the disk copy. A save
/// storage cannot take goes to the disk the same way (<see cref="ToDiskAsync"/>). The disk failing is logged
/// and never reaches her either. The page's own actions pause it (<see cref="PauseAsync"/>), so a write never
/// lands across a save; the page removes the copy, from storage and the disk, once a save goes through, and
/// whenever it replaces the form from storage (<see cref="RemoveAsync"/>).
/// </para>
/// Everything but the timer's callback runs on the page's circuit, one thing at a time, so it needs no lock.
/// </remarks>
/// <param name="client">Whose campaigns: the disk copies are kept by client and campaign id.</param>
/// <param name="onCircuit">Runs work on the page's circuit and then renders the page (the page's own InvokeAsync).</param>
/// <param name="started">
/// A new campaign was given its id at her first change (owner, 2026-11-03): the page moves its address to the
/// campaign's own, so a reload opens it again.
/// </param>
public sealed class WorkingCopyKeeper(
    WorkingCopies copies, IWorkingCopyDisk disk, ClientName client, TimeProvider clock, WorkingCopyOptions options,
    Func<Func<Task>, Task> onCircuit, Func<Guid, Task> started, ILogger log) : IDisposable
{
    private DraftSession? _session;

    // The form at its last look, to tell a change; null with nothing to keep.
    private string? _seen;

    // What the working copy holds as far as this page knows; null for none.
    private string? _written;

    // What this page's next write must find there: its own last write, none, or anything when it cannot know.
    private BlobExpected _expected = BlobExpected.Anything;

    // A copy is on the disk that storage does not hold yet.
    private bool _onDisk;

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

    /// <summary>
    /// On opening a saved campaign: what she had not saved, brought into the form, or null when there is
    /// nothing to bring back. The newest of the working copy in storage and one on the server's disk, while it
    /// is newer than the newest save and not the same as it; a disk copy is taken into storage now, and goes
    /// from the disk, or stays there when storage cannot take it. An older disk copy is dropped. Storage or the
    /// disk failing is logged, and the campaign opens as storage has it.
    /// </summary>
    public async Task<WorkingCopy?> OpenAsync(DraftSession session)
    {
        var id = session.Id!.Value;
        WorkingCopy? stored = null;
        var known = true;
        try
        {
            stored = await copies.ReadAsync(id);
        }
        catch (Exception ex) when (StorageFailed.Is(ex) || ex is InvalidDataException)
        {
            log.LogWarning(ex, "Could not read the working copy of campaign {Campaign}; the campaign opens as saved.", id);
            known = false;
        }
        var back = stored is not null && !session.IsNewestSave(stored.Json) ? stored : null;

        if (await OnDiskAsync(() => disk.ReadAsync(client, id), null, id) is { } onDisk)
        {
            var newer = (session.Latest is not { } latest || onDisk.WrittenAt > latest.SavedAt)
                        && (stored is null || onDisk.WrittenAt > stored.WrittenAt)
                        && !session.IsNewestSave(onDisk.Json);
            if (newer)
            {
                back = new WorkingCopy(onDisk.Draft, onDisk.Json, onDisk.BasedOn, onDisk.WrittenAt, ETag: "");
                try
                {
                    // Whatever is there: an unconditional write always answers with the new version.
                    var etag = await copies.WriteAsync(id, back.Draft, onDisk.BasedOn, BlobExpected.Anything);
                    stored = back = back with { ETag = etag! };
                    known = true;
                    await RemoveFromDiskAsync(id);
                }
                catch (Exception ex) when (StorageFailed.Is(ex))
                {
                    log.LogWarning(ex, "Could not take the disk copy of campaign {Campaign} into storage; it stays on the disk.", id);
                    stored = null;
                    known = false;
                    _onDisk = true;
                }
            }
            else
            {
                // Storage holds something as new: it was taken in already, or saved over.
                await RemoveFromDiskAsync(id);
            }
        }

        if (back is not null) session.BringBack(back.Draft);
        Track(session, stored, known);
        return back;
    }

    // The form's content, while there is a campaign on the page.
    private string? Form() => _session is { } session ? CampaignJson.SerializeDraft(session.Editor.Draft) : null;

    /// <summary>
    /// After each render of the page: when the form has changed since the last look, the write is set for
    /// <see cref="WorkingCopyOptions.Debounce"/> from now, or <see cref="WorkingCopyOptions.Cap"/> from the first
    /// change not written yet, whichever comes first. On a new campaign, the first change gives it its id and
    /// its address and is written at once (owner, 2026-11-03): until then nothing is kept, as nothing was typed.
    /// Never throws.
    /// </summary>
    public async Task LookedAsync()
    {
        if (IsDisposed || _paused > 0) return;
        var form = Form();
        if (form is null) return;
        if (form == _seen)
        {
            // Opened with a copy on the disk that storage could not take: storage is tried again after the cap.
            if (_onDisk && _timer is null) Schedule(options.Cap);
            return;
        }
        _seen = form;
        if (_session!.Id is null)
        {
            try
            {
                var id = _session.TakeId();
                await started(id);
                await (_writing = WriteAsync());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Could not start keeping the working copy of a new campaign.");
            }
            return;
        }
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
        if (IsDisposed || _paused > 0 || _session is not { Id: { } id } session) return;
        _since = null;
        // Taken whole now, before anything is awaited. Never saved, it started on no save.
        var draft = session.Editor.Draft;
        var json = CampaignJson.SerializeDraft(draft);
        var basedOn = session.Latest?.SavedAt;
        var title = session.Editor.Title;
        if (session.IsNewestSave(json))
        {
            // Changed back to what was saved: nothing unsaved is left to keep, so the copy of what was goes.
            if (_written is not null || _onDisk) await RemoveAsync(id);
            return;
        }
        if (json == _written) return;
        try
        {
            var etag = await copies.WriteAsync(id, draft, basedOn, _expected, title);
            if (etag is null)
            {
                // Another tab wrote or removed it since: said, and then the last writer wins.
                OtherTab = true;
                etag = await copies.WriteAsync(id, draft, basedOn, BlobExpected.Anything, title);
            }
            _expected = etag is null ? BlobExpected.Anything : BlobExpected.Version(etag);
            _written = json;
            // Storage holds the form now, newer than anything on the disk.
            if (_onDisk) await RemoveFromDiskAsync(id);
        }
        catch (Exception ex) when (StorageFailed.Is(ex))
        {
            log.LogWarning(ex, "Could not write the working copy of campaign {Campaign}; it goes to the disk and is tried again.", id);
            await WriteToDiskAsync(id, json, basedOn, title);
            _since ??= clock.GetUtcNow();
            Schedule(options.Cap);
        }
    }

    /// <summary>
    /// A save storage could not take: the form goes to the server's disk, so it is there to bring back however
    /// the page is left. Nothing when the form is the newest save, or the campaign has no id. Never throws.
    /// </summary>
    public async Task ToDiskAsync()
    {
        if (_session is not { Id: { } id } session) return;
        var json = CampaignJson.SerializeDraft(session.Editor.Draft);
        if (session.IsNewestSave(json)) return;
        await WriteToDiskAsync(id, json, session.Latest?.SavedAt, session.Editor.Title);
    }

    // With the title, for the campaigns list's row of a campaign never saved, kept in the copy's sidecar.
    private async Task WriteToDiskAsync(Guid id, string json, DateTimeOffset? basedOn, string? title)
    {
        if (await OnDiskAsync(async () =>
            {
                await disk.WriteAsync(client, new DiskCopy(id, json, basedOn, clock.GetUtcNow(), title));
                return true;
            }, false, id))
            _onDisk = true;
    }

    private Task RemoveFromDiskAsync(Guid id) => OnDiskAsync(async () =>
    {
        await disk.RemoveAsync(client, id);
        _onDisk = false;
        return true;
    }, false, id);

    // The server's disk failing is logged and never reaches her: the answer is then the fallback given.
    private async Task<T> OnDiskAsync<T>(Func<Task<T>> work, T failed, Guid id)
    {
        try
        {
            return await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "The server's disk failed for the working copy of campaign {Campaign}.", id);
            return failed;
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
    public Task ResumeAsync(DraftSession? session)
    {
        if (_paused > 0) _paused--;
        _session = session;
        // A change during the action, or one the action made, is seen at once.
        _seen = null;
        return LookedAsync();
    }

    /// <summary>
    /// The page saved, or replaced its form from storage: the working copy goes, from storage and the disk.
    /// Storage failing is logged and leaves it to the sweep, or to the page's next write over it.
    /// </summary>
    public async Task RemoveAsync(Guid campaignId)
    {
        await RemoveFromDiskAsync(campaignId);
        try
        {
            await copies.RemoveAsync(campaignId);
            _written = null;
            _expected = BlobExpected.Nothing;
            OtherTab = false;
        }
        catch (Exception ex) when (StorageFailed.Is(ex))
        {
            log.LogWarning(ex, "Could not remove the working copy of campaign {Campaign}; the sweep will.", campaignId);
            _expected = BlobExpected.Anything;
        }
    }

    /// <summary>
    /// "Discard": the working copy removed, from storage and the disk, as she asked. Storage failing throws, so
    /// the page can say it did not happen.
    /// </summary>
    public async Task DiscardAsync(Guid campaignId)
    {
        await copies.RemoveAsync(campaignId);
        _written = null;
        _expected = BlobExpected.Nothing;
        OtherTab = false;
        await RemoveFromDiskAsync(campaignId);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
    }
}
