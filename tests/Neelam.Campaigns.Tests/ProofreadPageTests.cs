using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Neelam.Campaigns.Storage;
using Neelam.Web.Components.Pages;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The demo with the AI proofread switched on, as the test site will be once the key is in Key Vault:
/// Prototype access, in-memory storage, and a fake proofreader in place of Claude, so nothing leaves
/// the test.
/// </summary>
public sealed class ProofreadDemoApp : InMemoryApp
{
    internal FakeProofreader Proofreader { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(PrototypeCallerSource.ClientSetting, DemoApp.Client.ToString());
        base.ConfigureWebHost(builder);
    }

    // Program's own access stands (Prototype, the demo); the proofread is on, and fake.
    protected override void ConfigureAccess(IServiceCollection services)
    {
        services.RemoveAll<ProofreadSwitch>().AddSingleton(new ProofreadSwitch(true));
        services.RemoveAll<IProofreader>().AddSingleton<IProofreader>(Proofreader);
    }
}

/// <summary>
/// What she sees of the AI proofread on the campaign page (owner, 2026-10-10): "Proofread with AI" on a
/// saved version; once proofread, its findings with the rules' and its photos' notes; the export then
/// says proofread, and the JSON too; a Must fix it found stops the export. Read by named elements.
/// </summary>
public class ProofreadPageTests(ProofreadDemoApp app) : IClassFixture<ProofreadDemoApp>
{
    private CampaignStore Store => app.Stores.Campaigns(DemoApp.Client, Actor.Demo);

    private static readonly Finding Unclear = new(Severity.Warning, "ai-clarity", "Offer", "Could read two ways. Suggest: say per visit.", "per visit");

    private async Task<string> Get(string path)
    {
        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<SaveRef> Saved(CampaignDraft? draft = null)
    {
        var save = await Store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", draft ?? DraftFixtures.Finished());
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return save;
    }

    private async Task Proofread(SaveRef save, params Finding[] findings) =>
        await Store.SaveProofreadAsync(save, new ProofreadRecord(app.Clock.GetUtcNow(), findings,
            [new PhotoCheck("Principals toasting", "Couldn't check this photo: it is not a Square photo in your image library.", false)]));

    [Fact]
    public async Task A_saved_version_offers_proofread_with_ai()
    {
        var save = await Saved();

        var page = await Get($"/campaigns/{save.Id}");

        Assert.Equal("Proofread with AI", RenderedPage.Named(page, "proofread-button"));
        Assert.StartsWith("It reads this saved version once, when you click", RenderedPage.Named(page, "proofread-status"));
    }

    [Fact]
    public async Task A_proofread_version_shows_what_it_found_with_the_rules_and_offers_no_second_run()
    {
        var save = await Saved();
        await Proofread(save, Unclear);

        var page = await Get($"/campaigns/{save.Id}");

        Assert.Equal(0, RenderedPage.Count(page, "proofread-button"));
        Assert.EndsWith("The AI proofread found 1 thing in this version, listed with the rules' findings.", RenderedPage.Named(page, "proofread-status"));
        Assert.StartsWith("These are the instant rules, with what the AI proofread found", RenderedPage.Named(page, "checks-with-proofread"));
        Assert.Contains(Unclear.Message, RenderedPage.Text(page));
        Assert.Equal("Principals toasting Couldn't check this photo: it is not a Square photo in your image library.",
            RenderedPage.Named(page, "proofread-photos"));
    }

    [Fact]
    public async Task Proofread_and_approved_the_export_says_proofread_and_so_does_the_json()
    {
        var save = await Saved();
        await Proofread(save, Unclear);
        var approved = await Store.ApproveAsync(save, "Priya");
        await Store.WarningsSeenAtExportAsync(approved, [.. SeenAtExport.KeysOf(DraftFixtures.Finished()), Unclear.SeenKey]);

        var page = await Get($"/campaigns/{save.Id}");

        Assert.StartsWith("Approved and proofread by AI", RenderedPage.Named(page, "export-proofread"));
        Assert.DoesNotContain("Not proofread by AI yet", RenderedPage.Text(page));
        var href = RenderedPage.Attribute(page, "assistant-download", "href");
        var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(href[(href.IndexOf(',') + 1)..])))!;
        Assert.True((bool?)json["review"]!["proofread"]);
        Assert.Null(json["review"]!["notice"]);
    }

    [Fact]
    public async Task Approved_but_not_proofread_the_export_is_still_marked_not_proofread()
    {
        var save = await Saved();
        var approved = await Store.ApproveAsync(save, "Priya");
        await Store.WarningsSeenAtExportAsync(approved, SeenAtExport.KeysOf(DraftFixtures.Finished()));

        var page = await Get($"/campaigns/{save.Id}");

        Assert.Equal(0, RenderedPage.Count(page, "export-proofread"));
        Assert.Contains("Not proofread by AI yet", RenderedPage.Text(page));
        Assert.Contains("This version has not been proofread by AI", RenderedPage.Text(page));
    }

    // What the AI found that is worth a look comes up at export, with the rules' own.
    [Fact]
    public async Task Its_warning_is_listed_before_the_export()
    {
        var save = await Saved();
        await Proofread(save, Unclear);
        await Store.ApproveAsync(save, "Priya");

        var page = await Get($"/campaigns/{save.Id}");

        var list = page[page.IndexOf("Before you copy it into Square", StringComparison.Ordinal)..];
        Assert.Contains(Unclear.Message, RenderedPage.Text(list));
    }

    [Fact]
    public async Task Its_must_fix_stops_the_export_and_says_why()
    {
        var save = await Saved();
        await Proofread(save, new Finding(Severity.Blocker, "ai-spelling", "Opening", "Misspelt. Suggest: receive", "recieve"));
        await Store.ApproveAsync(save, "Priya");

        var page = await Get($"/campaigns/{save.Id}");

        Assert.Equal("The AI proofread found something to fix in this version. Fix it, save, and approve the new version.",
            RenderedPage.Named(page, "export-waits-for-proofread-fix"));
        Assert.Equal(0, RenderedPage.Count(page, "assistant-download"));
        Assert.DoesNotContain("Copy into Square", RenderedPage.Text(page));
    }

    [Fact]
    public async Task With_the_proofread_on_the_banner_no_longer_says_it_is_off() =>
        Assert.Equal("This is a demo. What you save here is kept on a test system, not your own account.",
            RenderedPage.Named(await Get("/campaigns"), "demo-banner"));
}

/// <summary>
/// The page as she has it once she clicks "Proofread with AI" (owner, 2026-10-10, on staging: the
/// result read twice). A request only ever renders the page afresh, never what a click leaves, so the
/// page is rendered here as the server holds it and the button's own click handler is dispatched.
/// The proofreader is a fake; the stores are the app's in memory, so a request reopens what the click
/// kept. Read by named elements.
/// </summary>
public class ProofreadClickTests(ProofreadDemoApp app) : IClassFixture<ProofreadDemoApp>
{
    private static readonly Finding Unclear = new(Severity.Warning, "ai-clarity", "Offer", "Could read two ways. Suggest: say per visit.", "per visit");
    private const string FoundOne = "The AI proofread found 1 thing in this version, listed with the rules' findings.";

    private async Task<SaveRef> Saved()
    {
        var save = await app.Stores.Campaigns(DemoApp.Client, Actor.Demo).SaveDraftAsync(Guid.NewGuid(), "Hello", DraftFixtures.Finished());
        app.Clock.Now += TimeSpan.FromSeconds(1);
        return save;
    }

    private static int Occurrences(string text, string sentence) =>
        (text.Length - text.Replace(sentence, "", StringComparison.Ordinal).Length) / sentence.Length;

    [Fact]
    public async Task Just_proofread_the_section_says_what_it_found_once_and_so_it_does_on_reopening()
    {
        var save = await Saved();
        await using var page = await OpenPage.OpenAsync(app, save.Id, new FakeProofreader(Unclear));
        Assert.Equal("Proofread with AI", RenderedPage.Named(await page.HtmlAsync(), "proofread-button"));

        await page.ClickAsync("proofread-button");

        var html = await page.HtmlAsync();
        Assert.EndsWith(FoundOne, RenderedPage.Named(html, "proofread-status"));
        Assert.Equal(0, RenderedPage.Count(html, "proofread-message"));
        Assert.Equal(1, Occurrences(RenderedPage.Named(html, "proofread"), FoundOne));

        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/campaigns/{save.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reopened = await response.Content.ReadAsStringAsync();
        Assert.EndsWith(FoundOne, RenderedPage.Named(reopened, "proofread-status"));
        Assert.Equal(0, RenderedPage.Count(reopened, "proofread-message"));
        Assert.Equal(1, Occurrences(RenderedPage.Named(reopened, "proofread"), FoundOne));
    }

    // A failure is the click's alone to say: nothing was kept, so the line above still offers the proofread.
    [Fact]
    public async Task A_failed_proofread_is_said_once_under_the_offer()
    {
        var save = await Saved();
        await using var page = await OpenPage.OpenAsync(app, save.Id, new FakeProofreader { Throws = new HttpRequestException("down") });

        await page.ClickAsync("proofread-button");

        var html = await page.HtmlAsync();
        const string couldNotRun = "The AI proofread could not run. The rules' checks above still stand.";
        Assert.Equal(couldNotRun, RenderedPage.Named(html, "proofread-message"));
        Assert.StartsWith("It reads this saved version once, when you click", RenderedPage.Named(html, "proofread-status"));
        Assert.Equal(1, Occurrences(RenderedPage.Named(html, "proofread"), couldNotRun));
    }

    // The renderer's types are Blazor's own, and may change between releases (BL0006). Here they are
    // the only way to hold the page between clicks, and a change shows up as these tests failing.
#pragma warning disable BL0006
    /// <summary>
    /// CampaignEdit rendered with the app's own services, in one scope as a circuit has, with the
    /// given proofreader in place of the app's.
    /// </summary>
    private sealed class OpenPage : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;
        private readonly PageRenderer _renderer;
        private readonly int _root;

        private OpenPage(AsyncServiceScope scope, PageRenderer renderer, int root) =>
            (_scope, _renderer, _root) = (scope, renderer, root);

        public static async Task<OpenPage> OpenAsync(ProofreadDemoApp app, Guid id, IProofreader proofreader)
        {
            var scope = app.Services.CreateAsyncScope();
            // Who is asking, set as a circuit sets it when it starts: nobody signed in, as on the demo.
            ((IHostEnvironmentAuthenticationStateProvider)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>())
                .SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
            var services = new WithProofreader(scope.ServiceProvider, proofreader);
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

        public Task<string> HtmlAsync() => _renderer.Dispatcher.InvokeAsync(() =>
        {
            _renderer.ThrowIfFailed();
            return _renderer.Html(_root);
        });

        public async ValueTask DisposeAsync()
        {
            await _renderer.Dispatcher.InvokeAsync(_renderer.Dispose);
            await _scope.DisposeAsync();
        }
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
#pragma warning restore BL0006

    private sealed class WithProofreader(IServiceProvider services, IProofreader proofreader) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IProofreader) ? proofreader
            : serviceType == typeof(IServiceProvider) ? this
            : services.GetService(serviceType);
    }
}

/// <summary>While the proofread is off, as on the demo until the key is set, nothing offers it.</summary>
public class ProofreadOffPageTests(DemoApp app) : IClassFixture<DemoApp>
{
    [Fact]
    public async Task No_proofread_is_offered_and_the_page_says_it_is_not_switched_on()
    {
        var save = await app.Stores.Campaigns(DemoApp.Client, Actor.Demo).SaveDraftAsync(Guid.NewGuid(), "Hello", DraftFixtures.Finished());

        var response = await app.CreateClient().GetAsync($"/campaigns/{save.Id}");
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(0, RenderedPage.Count(page, "proofread-button"));
        Assert.Equal(0, RenderedPage.Count(page, "proofread-status"));
        Assert.Contains("The AI proofread is not switched on yet.", RenderedPage.Text(page));
        Assert.False(app.Services.GetRequiredService<ProofreadSwitch>().IsOn);
    }
}
