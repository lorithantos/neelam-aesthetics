using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;

namespace Neelam.Web;

/// <summary>
/// A page or section that shows a document another tab or person may change while it is open
/// (2026-10-11, the owner's "concurrent usage by two users"): the campaign and template editors, the
/// Look page, the baseline and ladders sections. It owns the page's <see cref="PageWatch{T}"/>: made on
/// the first <see cref="Watch"/>, started once the page is live on its circuit, never while it is
/// rendered ahead of time, and disposed with the circuit, so the timer stops and the page stops listening.
/// Each page's own actions go through <see cref="ActingAsync(Func{Task})"/>. One copy since 2026-11-03; five
/// pages wired it by hand before.
/// </summary>
/// <typeparam name="T">One version as storage lists it: a <see cref="SaveRef"/> or a <see cref="DocumentVersion"/>.</typeparam>
public abstract class WatchedComponent<T> : ComponentBase, IDisposable where T : class
{
    [Inject]
    protected CampaignSignals Signals { get; set; } = default!;

    [Inject]
    protected TimeProvider Clock { get; set; } = default!;

    [Inject]
    protected IOptions<CampaignWatchOptions> WatchOptions { get; set; } = default!;

    [Inject]
    protected ILoggerFactory Logs { get; set; } = default!;

    /// <summary>
    /// The page's look at storage: at once on this instance (<see cref="CampaignSignals"/>), every
    /// <c>CampaignWatch:Interval</c> from anywhere, and after each of the page's own actions. Null until
    /// the page first watches something.
    /// </summary>
    protected PageWatch<T>? Watcher { get; private set; }

    /// <summary>What storage says changed since the page's version, by its last read.</summary>
    protected CampaignChange StorageChange => Watcher?.Change ?? CampaignChange.None;

    /// <summary>
    /// Watches this from now (<see cref="PageWatch{T}.Watch"/>); null, or something not in storage yet,
    /// watches nothing. The first call makes the watch, with what the page was just opened with counting
    /// as storage's as of now.
    /// </summary>
    protected void Watch(IWatched<T>? watched)
    {
        Watcher ??= new PageWatch<T>(Signals, Clock, WatchOptions.Value.Interval, OnCircuitAsync, Logs.CreateLogger(GetType()));
        Watcher.Watch(watched);
    }

    /// <summary>News from a timer or a signal, on this page's circuit, and the page drawn again with it.</summary>
    protected Task OnCircuitAsync(Func<Task> work) => InvokeAsync(async () =>
    {
        await work();
        StateHasChanged();
    });

    // Live on its circuit: listening and the timer start, once.
    protected override void OnAfterRender(bool firstRender) => Watcher?.Start();

    /// <summary>
    /// One of the page's own actions: what storage says while it runs is not news, and storage is read
    /// again once it is done, so the page knows where it stands, someone else's change included.
    /// </summary>
    protected async Task ActingAsync(Func<Task> action)
    {
        Watcher?.Begin();
        try
        {
            await action();
        }
        finally
        {
            if (Watcher is not null) await Watcher.EndAsync();
        }
    }

    /// <summary>
    /// One of the page's own actions that reads or writes storage, as <see cref="ActingAsync(Func{Task})"/>,
    /// where storage failing (<see cref="StorageFailed.Is"/>) never ends her session (2026-11-03): it is
    /// logged, and <paramref name="say"/> is given <see cref="StorageFailed.NotDone"/> with
    /// <paramref name="notDone"/>, such as "Not saved". The action changes what the page shows only once
    /// storage has answered, so the form is left exactly as she typed it. Anything else still escapes.
    /// </summary>
    /// <param name="failed">After storage failed and she was told: the campaign page's save keeps the form on the server's disk.</param>
    protected async Task ActingAsync(Func<Task> action, string notDone, Action<string> say, Func<Task>? failed = null)
    {
        Watcher?.Begin();
        await BeforeActingAsync();
        try
        {
            await action();
        }
        catch (Exception ex) when (StorageFailed.Is(ex))
        {
            Logs.CreateLogger(GetType()).LogError(ex, "{Action}: storage failed under the page's own action; the page is left as it was.", notDone);
            say(StorageFailed.NotDone(notDone));
            if (failed is not null) await failed();
        }
        finally
        {
            await AfterActingAsync();
            if (Watcher is not null) await Watcher.EndAsync();
        }
    }

    /// <summary>Before each of the page's own actions (<see cref="ActingAsync(Func{Task}, string, Action{string})"/>): the campaign page pauses its working copy.</summary>
    protected virtual Task BeforeActingAsync() => Task.CompletedTask;

    /// <summary>After each of them, done or not: the campaign page keeps its working copy again.</summary>
    protected virtual Task AfterActingAsync() => Task.CompletedTask;

    /// <summary>
    /// One of her events on the page, a change in a field or a click, for a page that implements
    /// <see cref="IHandleEvent"/> and hands each one here: drawn as <see cref="ComponentBase"/> draws an event,
    /// at once while it runs and again once it is done, with <paramref name="then"/> once it has run, done or
    /// not. Fields inside the page tell it of theirs (<see cref="FieldsComponent"/>). Only she makes events;
    /// what the page does by itself (opening, the watch, another tab's news) comes by other ways and never
    /// reaches <paramref name="then"/>.
    /// </summary>
    protected async Task HandleHerEventAsync(EventCallbackWorkItem callback, object? arg, Action then)
    {
        var handled = callback.InvokeAsync(arg);
        if (!handled.IsCompleted) StateHasChanged();
        try
        {
            await handled;
        }
        catch when (handled.IsCanceled)
        {
            // As ComponentBase: a handler cancelled is not drawn again.
            return;
        }
        finally
        {
            then();
        }
        StateHasChanged();
    }

    /// <summary>With the circuit, after the watch: what else the page holds that must stop with it.</summary>
    protected virtual void Disposing()
    {
    }

    /// <summary>
    /// One of the page's own actions after which the page loads again (the Look page: the look is drawn in
    /// each page's head): <paramref name="action"/> gives the address to load, and storage is not read
    /// again, since the page is about to be replaced; or null when nothing was changed, and then it is read
    /// again as for any action.
    /// </summary>
    protected async Task ActingAsync(Func<Task<string?>> action, Action<string> loadAgain)
    {
        Watcher?.Begin();
        string? address = null;
        try
        {
            address = await action();
        }
        finally
        {
            if (address is null && Watcher is not null) await Watcher.EndAsync();
        }
        if (address is not null) loadAgain(address);
    }

    // With the circuit: the timer stops and the page stops listening.
    public void Dispose()
    {
        Watcher?.Dispose();
        Disposing();
        GC.SuppressFinalize(this);
    }
}

/// <summary>When a page's "modified" notice offers Keep mine beside "Load the latest" (owner, 2026-10-11).</summary>
public enum KeepMineWhen
{
    /// <summary>Not offered: the template and Look pages.</summary>
    Never,

    /// <summary>When the document was saved again or the page's version undone: the campaign page.</summary>
    OnChange,

    /// <summary>Once the page's own save was refused: the baseline and ladders sections.</summary>
    AfterRefusal,
}
