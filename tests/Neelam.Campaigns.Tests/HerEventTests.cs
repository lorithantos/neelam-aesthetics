using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Neelam.Campaigns.Storage;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// What a page that takes her every event (<see cref="WatchedComponent{T}.HandleHerEventAsync"/>) does with
/// one: drawn exactly as ComponentBase draws an event, and told once it has run, done or not, without what it
/// is told ever hiding what went wrong in the event itself. A page of one button, held as its circuit holds it.
/// </summary>
public class HerEventTests(DemoApp app) : IClassFixture<DemoApp>
{
    private sealed class Seen
    {
        public int Draws;
        public int Told;
    }

    private sealed class Probe : WatchedComponent<SaveRef>, IHandleEvent
    {
        [Parameter, EditorRequired] public Func<Task> Handler { get; set; } = null!;

        [Parameter, EditorRequired] public Seen Seen { get; set; } = null!;

        [Parameter] public Action? Then { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            Seen.Draws++;
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "data-testid", "act");
            builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, Handler));
            builder.CloseElement();
        }

        Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg) => HandleHerEventAsync(callback, arg, () =>
        {
            Seen.Told++;
            Then?.Invoke();
        });
    }

    private async Task<(OpenCampaignPage Page, Seen Seen)> Open(Func<Task> handler, Action? then = null)
    {
        var seen = new Seen();
        var page = await OpenCampaignPage.OpenAsync<Probe>(app, new Dictionary<string, object?>
        {
            [nameof(Probe.Handler)] = handler,
            [nameof(Probe.Seen)] = seen,
            [nameof(Probe.Then)] = then,
        });
        Assert.Equal(1, seen.Draws);
        return (page, seen);
    }

    [Fact]
    public async Task An_event_done_at_once_is_drawn_once_and_told()
    {
        var (page, seen) = await Open(() => Task.CompletedTask);
        await using var _ = page;

        await page.ClickAsync("act");

        Assert.Equal(2, seen.Draws);
        Assert.Equal(1, seen.Told);
    }

    [Fact]
    public async Task An_event_still_running_is_drawn_at_once_and_again_once_done_and_told_then()
    {
        var running = new TaskCompletionSource();
        var (page, seen) = await Open(() => running.Task);
        await using var _ = page;

        var click = page.ClickAsync("act");
        await page.HtmlAsync();
        Assert.Equal(2, seen.Draws);
        Assert.Equal(0, seen.Told);

        running.SetResult();
        await click;

        Assert.Equal(3, seen.Draws);
        Assert.Equal(1, seen.Told);
    }

    [Fact]
    public async Task An_event_that_fails_at_once_is_drawn_told_and_still_fails()
    {
        var failure = new InvalidOperationException("the handler's own");
        var (page, seen) = await Open(() => Task.FromException(failure));
        await using var _ = page;

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => page.ClickAsync("act")));
        Assert.Equal(2, seen.Draws);
        Assert.Equal(1, seen.Told);
    }

    [Fact]
    public async Task What_it_is_told_never_hides_what_went_wrong_in_the_event()
    {
        var failure = new InvalidOperationException("the handler's own");
        var (page, seen) = await Open(() => Task.FromException(failure), then: () => throw new ArgumentException("told"));
        await using var _ = page;

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => page.ClickAsync("act")));
        Assert.Equal(1, seen.Told);
    }

    [Fact]
    public async Task An_event_cancelled_at_once_is_drawn_once_and_told_and_nothing_fails()
    {
        var (page, seen) = await Open(() => Task.FromCanceled(new CancellationToken(canceled: true)));
        await using var _ = page;

        await page.ClickAsync("act");

        Assert.Equal(2, seen.Draws);
        Assert.Equal(1, seen.Told);
    }

    [Fact]
    public async Task An_event_cancelled_while_running_is_drawn_once_and_told_and_nothing_fails()
    {
        var running = new TaskCompletionSource();
        var (page, seen) = await Open(() => running.Task);
        await using var _ = page;

        var click = page.ClickAsync("act");
        await page.HtmlAsync();
        running.SetCanceled();
        await click;

        Assert.Equal(2, seen.Draws);
        Assert.Equal(1, seen.Told);
    }
}
