using Neelam.Campaigns.Storage;

namespace Neelam.Web;

/// <summary>
/// One editor page's tab among <see cref="EditingTabs"/>, so the page can say its campaign, template or look
/// is also being edited in another tab as soon as that tab types, not only once both have written (the
/// owner's browser check, 2026-11-05). The page opens it once it is live on its circuit, never while it is
/// rendered ahead of time (that page is thrown away, in a request's scope rather than the circuit's
/// <see cref="TabCircuit"/>), and again whenever the subject it shows changes; disposed with the page.
/// </summary>
/// <remarks>
/// Another tab's editing is drawn on the page's own circuit (<paramref name="onCircuit"/>, the page's
/// InvokeAsync and render), never on the thread that worked it out. News arriving as the page closes, which
/// <see cref="EditingTabs"/> may still send once, is dropped, as <see cref="PageWatch{T}"/> drops a late
/// check; anything else is logged by its type only, as <see cref="EditingTabs"/> logs a tab's failure,
/// since a page's exception may carry what it shows.
/// </remarks>
/// <param name="onCircuit">Runs work on the page's circuit and then renders the page (WatchedComponent.OnCircuitAsync).</param>
public sealed class EditingTabNotice(EditingTabs tabs, TabCircuit circuit, Func<Func<Task>, Task> onCircuit, ILogger log) : IDisposable
{
    private EditingTab? _tab;
    private int _disposed;

    /// <summary>Whether another tab is editing what this page shows, as last worked out; false before the page opens it.</summary>
    public bool OthersEditing => _tab?.OthersEditing == true;

    /// <summary>
    /// The page is live and shows <paramref name="subject"/>: its tab opens, or stays as it is when it already
    /// shows that, or moves from what it showed before (that tab closes). Nothing once the page has closed.
    /// </summary>
    public void Open(ClientName client, TabSubject subject)
    {
        if (IsDisposed || _tab is { } open && open.Client == client && open.Subject == subject) return;
        _tab?.Dispose();
        _tab = tabs.Register(client, subject, circuit.Id, Changed);
        // Closed meanwhile on another thread: the tab just opened goes with it.
        if (IsDisposed) _tab.Dispose();
    }

    /// <summary>She changed what the page shows: the other tabs on it are told she is editing. Nothing before the page opens its tab.</summary>
    public void Typed() => _tab?.Typed();

    // From EditingTabs, on whatever thread flipped the notice: the page is drawn again on its circuit.
    private void Changed()
    {
        if (IsDisposed) return;
        _ = OnCircuitAsync();
    }

    private async Task OnCircuitAsync()
    {
        try
        {
            await onCircuit(() => Task.CompletedTask);
        }
        catch (Exception ex) when (IsDisposed || ex is ObjectDisposedException or OperationCanceledException)
        {
            // The page closed meanwhile: nobody is left to tell.
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not show an open page that another tab's editing changed: {Error}.", ex.GetType().Name);
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _tab?.Dispose();
    }
}
