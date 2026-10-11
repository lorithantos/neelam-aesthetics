using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Neelam.Web.Components.Pages;

namespace Neelam.Campaigns.Tests;

// The renderer's types are Blazor's own, and may change between releases (BL0006). Here they are
// the only way to hold the page between clicks, and a change shows up as these tests failing.
#pragma warning disable BL0006
/// <summary>
/// CampaignEdit rendered with the app's own services, in one scope as a circuit has, held between
/// clicks as the server holds it: a request only ever renders the page afresh, never what a click
/// leaves. Two of these on one campaign are two tabs. Services can be put in place of the app's for
/// this page alone, such as a proofreader or a clock whose timers move only when told.
/// </summary>
internal sealed class OpenCampaignPage : IAsyncDisposable
{
    /// <summary>How long a wait for the page to show something may take before it fails.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly AsyncServiceScope _scope;
    private readonly PageRenderer _renderer;
    private readonly int _root;

    private OpenCampaignPage(AsyncServiceScope scope, PageRenderer renderer, int root) =>
        (_scope, _renderer, _root) = (scope, renderer, root);

    public static async Task<OpenCampaignPage> OpenAsync(InMemoryApp app, Guid id, params object[] replacing)
    {
        var scope = app.Services.CreateAsyncScope();
        // Who is asking, set as a circuit sets it when it starts: nobody signed in, as on the demo.
        ((IHostEnvironmentAuthenticationStateProvider)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>())
            .SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        var services = new Replacing(scope.ServiceProvider, [.. replacing, new NoBrowser()]);
        var renderer = new PageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync<CampaignEdit>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CampaignEdit.Id)] = (Guid?)id })));
        return new(scope, renderer, root);
    }

    /// <summary>Clicks the one element named <paramref name="testId"/>, through its own onclick.</summary>
    public Task ClickAsync(string testId) => _renderer.Dispatcher.InvokeAsync(async () =>
    {
        var handler = _renderer.Handler(_root, testId, "onclick")
            ?? throw new InvalidOperationException($"Nothing named {testId} can be clicked.");
        await _renderer.DispatchEventAsync(handler, null, new MouseEventArgs());
        _renderer.ThrowIfFailed();
    });

    /// <summary>Types <paramref name="value"/> into the one field named <paramref name="testId"/>, as each keystroke tells the page.</summary>
    public Task TypeAsync(string testId, string value) => _renderer.Dispatcher.InvokeAsync(async () =>
    {
        var handler = _renderer.Handler(_root, testId, "oninput")
            ?? throw new InvalidOperationException($"Nothing named {testId} can be typed into.");
        await _renderer.DispatchEventAsync(handler, null, new ChangeEventArgs { Value = value });
        _renderer.ThrowIfFailed();
    });

    public Task<string> HtmlAsync() => _renderer.Dispatcher.InvokeAsync(() =>
    {
        _renderer.ThrowIfFailed();
        return _renderer.Html(_root);
    });

    /// <summary>
    /// The page once <paramref name="shows"/> holds of it: for what arrives from elsewhere, a signal or
    /// a timer, on the page's own circuit. Fails after <see cref="Patience"/>, saying what it saw.
    /// </summary>
    public async Task<string> WaitForAsync(Func<string, bool> shows, string what)
    {
        var until = DateTime.UtcNow + Patience;
        while (true)
        {
            var html = await HtmlAsync();
            if (shows(html)) return html;
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"The page never showed {what}.");
            await Task.Delay(10);
        }
    }

    /// <summary>The page once an element named <paramref name="testId"/> is on it.</summary>
    public Task<string> WaitForAsync(string testId) => WaitForAsync(html => RenderedPage.Count(html, testId) > 0, testId);

    public async ValueTask DisposeAsync()
    {
        await _renderer.Dispatcher.InvokeAsync(_renderer.Dispose);
        await _scope.DisposeAsync();
    }

    /// <summary>
    /// A renderer that holds the page as the server does between clicks: every component made as it
    /// is, whatever render mode it names, and its current frames written out as HTML for
    /// <see cref="RenderedPage"/> to read.
    /// </summary>
    private sealed class PageRenderer(IServiceProvider services, ILoggerFactory logs) : Renderer(services, logs)
    {
        private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
            { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr" };

        private Exception? _failure;

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public async Task<int> RenderAsync<TComponent>(ParameterView parameters) where TComponent : IComponent
        {
            var id = AssignRootComponentId(InstantiateComponent(typeof(TComponent)));
            await RenderRootComponentAsync(id, parameters);
            ThrowIfFailed();
            return id;
        }

        public void ThrowIfFailed()
        {
            if (_failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        protected override IComponent ResolveComponentForRenderMode(
            Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            componentActivator.CreateInstance(componentType);

        protected override void HandleException(Exception exception) => _failure ??= exception;

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        public string Html(int componentId)
        {
            var html = new StringBuilder();
            WriteComponent(html, componentId);
            return html.ToString();
        }

        // The handler the element named testId has for eventName, wherever it is on the page.
        public ulong? Handler(int componentId, string testId, string eventName)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Component && Handler(frame.ComponentId, testId, eventName) is { } inner)
                    return inner;
                if (frame.FrameType != RenderTreeFrameType.Element) continue;
                var attributes = Attributes(frames.Array, i).ToList();
                if (!attributes.Any(a => a.AttributeName == "data-testid" && Equals(a.AttributeValue, testId))) continue;
                return attributes.FirstOrDefault(a => a.AttributeName == eventName && a.AttributeEventHandlerId != 0).AttributeEventHandlerId;
            }
            return null;
        }

        private static IEnumerable<RenderTreeFrame> Attributes(RenderTreeFrame[] frames, int element)
        {
            for (var j = element + 1; j < element + frames[element].ElementSubtreeLength && frames[j].FrameType == RenderTreeFrameType.Attribute; j++)
                yield return frames[j];
        }

        private void WriteComponent(StringBuilder html, int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            WriteFrames(html, frames.Array, 0, frames.Count);
        }

        private void WriteFrames(StringBuilder html, RenderTreeFrame[] frames, int start, int end)
        {
            for (var i = start; i < end;)
                i = WriteFrame(html, frames, i);
        }

        private int WriteFrame(StringBuilder html, RenderTreeFrame[] frames, int i)
        {
            var frame = frames[i];
            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Element:
                    var end = i + frame.ElementSubtreeLength;
                    html.Append('<').Append(frame.ElementName);
                    var child = i + 1;
                    foreach (var attribute in Attributes(frames, i))
                    {
                        child++;
                        if (attribute.AttributeEventHandlerId != 0) continue;
                        switch (attribute.AttributeValue)
                        {
                            case null or false:
                                break;
                            case true:
                                html.Append(' ').Append(attribute.AttributeName);
                                break;
                            default:
                                html.Append(' ').Append(attribute.AttributeName).Append("=\"")
                                    .Append(WebUtility.HtmlEncode(attribute.AttributeValue.ToString())).Append('"');
                                break;
                        }
                    }
                    html.Append('>');
                    if (Void.Contains(frame.ElementName)) return end;
                    WriteFrames(html, frames, child, end);
                    html.Append("</").Append(frame.ElementName).Append('>');
                    return end;
                case RenderTreeFrameType.Text:
                    html.Append(WebUtility.HtmlEncode(frame.TextContent));
                    return i + 1;
                case RenderTreeFrameType.Markup:
                    html.Append(frame.MarkupContent);
                    return i + 1;
                case RenderTreeFrameType.Component:
                    WriteComponent(html, frame.ComponentId);
                    return i + frame.ComponentSubtreeLength;
                case RenderTreeFrameType.Region:
                    WriteFrames(html, frames, i + 1, i + frame.RegionSubtreeLength);
                    return i + frame.RegionSubtreeLength;
                default:
                    return i + 1;
            }
        }
    }

    // No browser is attached: what the page asks of one (the address and the tab's title after a
    // save) answers nothing, as a browser that ignored it would.
    private sealed class NoBrowser : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask SetValueAsync<TValue>(string identifier, TValue value) => ValueTask.CompletedTask;

        public ValueTask SetValueAsync<TValue>(string identifier, TValue value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    // The app's services, with some put in place for this page alone, each under the types it is
    // registered as in the app: its own type, and TimeProvider for a clock.
    private sealed class Replacing(IServiceProvider services, object[] replacing) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProvider) ? this
            : replacing.FirstOrDefault(serviceType.IsInstanceOfType) ?? services.GetService(serviceType);
    }
}
#pragma warning restore BL0006
