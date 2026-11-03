using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.JSInterop;
using Neelam.Campaigns.Storage;
using Neelam.Web.Components.Shared;
using TemplatePage = Neelam.Web.Components.Pages.TemplateEdit;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Priya, 2026-10-31: "Block arrangement is terrible"; the owner, 2026-11-03: "create a set of proxy
/// blocks with the names that they can see on a single page that allows them to drag and drop". The
/// template page's Arrange blocks list: one chip per block, dragged (arrange.js, which no test here
/// runs: there is no browser) or moved a step with Up and Down. The drag reaches .NET as
/// <see cref="ArrangeBlocks.MoveBlock"/>, called here as the script calls it, through the reference the
/// page hands the browser. The template editor held as its circuit holds it (<see cref="OpenCampaignPage"/>).
/// </summary>
public class TemplateArrangePageTests(DemoApp app) : IClassFixture<DemoApp>
{
    // The membership template's blocks, top to bottom.
    private static readonly string[] Membership =
        ["Header", "Spacer", "Headline", "Greeting", "Opening", "Offer", "Closing", "Sign-off", "Disclaimer", "Photo", "Call to action"];

    // What the page asks of the browser, and the template it has open.
    private readonly Browser _browser = new();
    private readonly Guid _id = Guid.NewGuid();

    private async Task<OpenCampaignPage> OpenMembershipAsync()
    {
        await StalePages.Later(app).Campaigns(DemoApp.Client, Actor.Demo).SaveTemplateAsync(_id, DraftFixtures.Membership);
        return await OpenCampaignPage.OpenAsync<TemplatePage>(app,
            new Dictionary<string, object?> { [nameof(TemplatePage.Id)] = (Guid?)_id }, StalePages.Clock(app), _browser);
    }

    [Fact]
    public async Task Each_block_is_a_chip_in_order_named_by_its_label_and_its_type_when_that_differs()
    {
        await using var page = await OpenMembershipAsync();
        var html = await page.HtmlAsync();

        Assert.Equal(Membership, Chips(html));
        Assert.Equal("· Heading", RenderedPage.Named(html, "arrange-type-2"));
        Assert.Equal("· Tiered offer", RenderedPage.Named(html, "arrange-type-5"));
        Assert.Equal("· Fine print", RenderedPage.Named(html, "arrange-type-8"));
        // Named as its type already: the type is not said twice.
        Assert.Equal(0, RenderedPage.Count(html, "arrange-type-0"));
        Assert.Equal(0, RenderedPage.Count(html, "arrange-type-3"));
        // Above the blocks' own cards.
        Assert.True(html.IndexOf("data-testid=\"arrange-blocks\"", StringComparison.Ordinal)
                    < html.IndexOf("data-testid=\"block-card-0\"", StringComparison.Ordinal));
    }

    // Dropping a chip: the script calls MoveBlock(from, to). The form moves the block, the cards and
    // both previews follow, and nothing is saved.
    [Fact]
    public async Task A_chip_dropped_in_a_new_place_moves_the_block_in_the_form_and_saves_nothing()
    {
        await using var page = await OpenMembershipAsync();
        var arrange = await _browser.ArrangeAsync();

        // The headline, dropped below the opening.
        await arrange.Value.MoveBlock(2, 4);

        var html = await page.HtmlAsync();
        string[] moved = ["Header", "Spacer", "Greeting", "Opening", "Headline", "Offer", "Closing", "Sign-off", "Disclaimer", "Photo", "Call to action"];
        Assert.Equal(moved, Chips(html));
        Assert.Equal(moved, Cards(html));
        // A card's contents move with its block: the greeting's own preview is in the third card now.
        Assert.Equal("It goes out like this: Hi Beautiful 🤍", RenderedPage.Named(html, "fixed-preview-2"));
        var preview = RenderedPage.Named(html, "template-preview");
        Assert.True(preview.IndexOf("Hi Beautiful", StringComparison.Ordinal)
                    < preview.IndexOf("‹Headline: written for each campaign›", StringComparison.Ordinal));

        var store = StalePages.Later(app).Campaigns(DemoApp.Client, Actor.Demo);
        Assert.Single(await store.HistoryAsync(DocumentKind.Template, _id));
    }

    // A drop where the chip started, or a place the list no longer has, changes nothing.
    [Fact]
    public async Task A_drop_on_no_new_place_changes_nothing()
    {
        await using var page = await OpenMembershipAsync();
        var arrange = await _browser.ArrangeAsync();

        await arrange.Value.MoveBlock(3, 3);
        await arrange.Value.MoveBlock(3, Membership.Length);

        Assert.Equal(Membership, Chips(await page.HtmlAsync()));
    }

    // A moved block is an edit like any other: the form then differs from the save, and moving it back
    // undoes that.
    [Fact]
    public async Task A_move_is_an_unsaved_change_until_it_is_moved_back()
    {
        var store = StalePages.Later(app).Campaigns(DemoApp.Client, Actor.Demo);
        await store.SaveTemplateAsync(_id, DraftFixtures.Membership);
        var session = (await TemplateSession.OpenAsync(store, _id, Catalogs.Shipped))!;
        Assert.False(session.HasUnsavedChanges);

        session.Editor.MoveTo(2, 4);
        Assert.True(session.HasUnsavedChanges);
        session.Editor.MoveTo(4, 2);
        Assert.False(session.HasUnsavedChanges);
    }

    // Keyboard and screen readers: Up and Down on each chip, named for its block, with none past an end;
    // the blocks' own cards no longer have them.
    [Fact]
    public async Task Up_and_down_on_a_chip_move_its_block_a_step_and_the_cards_have_none()
    {
        await using var page = await OpenMembershipAsync();
        var html = await page.HtmlAsync();

        Assert.Equal("Move Headline up", RenderedPage.Attribute(html, "arrange-up-2", "aria-label"));
        Assert.Equal("Move Headline down", RenderedPage.Attribute(html, "arrange-down-2", "aria-label"));
        Assert.Single(Regex.Matches(html, "aria-label=\"Move Headline up\""));
        Assert.Single(Regex.Matches(html, "aria-label=\"Move Headline down\""));
        Assert.True(Disabled(html, "arrange-up-0"));
        Assert.False(Disabled(html, "arrange-down-0"));
        Assert.False(Disabled(html, "arrange-up-10"));
        Assert.True(Disabled(html, "arrange-down-10"));

        await page.ClickAsync("arrange-up-2");
        html = await page.HtmlAsync();
        Assert.Equal(["Header", "Headline", "Spacer"], Chips(html).Take(3));
        Assert.Equal(["Header", "Headline", "Spacer"], Cards(html).Take(3));

        await page.ClickAsync("arrange-down-1");
        await page.ClickAsync("arrange-down-2");
        html = await page.HtmlAsync();
        Assert.Equal(["Header", "Spacer", "Greeting", "Headline"], Chips(html).Take(4));
        Assert.Equal(["Header", "Spacer", "Greeting", "Headline"], Cards(html).Take(4));
    }

    // Blazor moves the chip's element, and a browser takes focus off an element that moves: the page
    // asks the browser to put it back on the moved chip's own button.
    [Fact]
    public async Task After_up_or_down_focus_goes_back_to_the_moved_chip_s_button()
    {
        await using var page = await OpenMembershipAsync();
        await _browser.ArrangeAsync();

        await page.ClickAsync("arrange-up-2");
        Assert.Equal(new object?[] { 1, "up" }, (await _browser.CallAsync("neelamArrange.focus"))[1..]);

        await page.ClickAsync("arrange-down-1");
        Assert.Equal(new object?[] { 2, "down" }, (await _browser.CallAsync("neelamArrange.focus", after: 1))[1..]);
    }

    [Fact]
    public async Task The_reference_the_browser_holds_is_let_go_with_the_page()
    {
        var page = await OpenMembershipAsync();
        var arrange = await _browser.ArrangeAsync();
        Assert.NotNull(arrange.Value);

        await page.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => arrange.Value);
    }

    // The script and the page name the same things: what .NET calls in the browser, and the method the
    // browser calls back, which only [JSInvokable] lets it reach.
    [Fact]
    public void The_script_and_the_page_agree_on_what_each_calls()
    {
        var web = Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web");
        var script = File.ReadAllText(Path.Combine(web, "wwwroot", "arrange.js"));
        var shell = File.ReadAllText(Path.Combine(web, "Components", "App.razor"));

        Assert.Contains("<script src=\"@Assets[\"arrange.js\"]\"></script>", shell);
        Assert.Contains("window.neelamArrange =", script);
        Assert.Contains("attach(list, dotnet) {", script);
        Assert.Contains("focus(list, index, which) {", script);
        Assert.Contains($"invokeMethodAsync(\"{nameof(ArrangeBlocks.MoveBlock)}\", d.from, d.to)", script);
        Assert.NotNull(typeof(ArrangeBlocks).GetMethod(nameof(ArrangeBlocks.MoveBlock))!.GetCustomAttribute<JSInvokableAttribute>());
        // No HTML5 drag and drop, which does not work on a touch screen.
        Assert.DoesNotContain("draggable", script);
        Assert.Contains("setPointerCapture", script);
    }

    private static IReadOnlyList<string> Chips(string html) =>
        RenderedPage.NamesStartingWith(html, "arrange-name-").Select(i => RenderedPage.Named(html, $"arrange-name-{i}")).ToList();

    private static IReadOnlyList<string> Cards(string html) =>
        RenderedPage.NamesStartingWith(html, "block-card-").Select(i => RenderedPage.Attribute(html, $"block-card-{i}", "aria-label")).ToList();

    // Whether the one element named testId carries disabled, which the page writes with no value.
    private static bool Disabled(string html, string testId)
    {
        var tag = Assert.Single(Regex.Matches(html, $"<[a-z]+\\b[^>]*\\sdata-testid=\"{Regex.Escape(testId)}\"[^>]*>"));
        return Regex.IsMatch(tag.Value, @"\sdisabled(?=[\s>=])");
    }

    /// <summary>
    /// A browser that answers nothing, as the one <see cref="OpenCampaignPage"/> puts in place does, and
    /// keeps what the page asked of it: the reference arrange.js would call back, and where focus goes.
    /// </summary>
    private sealed class Browser : IJSRuntime
    {
        private readonly List<(string Identifier, object?[] Args)> _calls = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            lock (_calls) _calls.Add((identifier, args ?? []));
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask SetValueAsync<TValue>(string identifier, TValue value) => ValueTask.CompletedTask;

        public ValueTask SetValueAsync<TValue>(string identifier, TValue value, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        /// <summary>The arguments of the page's call to <paramref name="identifier"/> after the first <paramref name="after"/> of them.</summary>
        public async Task<object?[]> CallAsync(string identifier, int after = 0)
        {
            var until = DateTime.UtcNow + OpenCampaignPage.Patience;
            while (true)
            {
                lock (_calls)
                {
                    if (_calls.Where(c => c.Identifier == identifier).Skip(after).FirstOrDefault() is { Identifier: not null } call)
                        return call.Args;
                }
                if (DateTime.UtcNow > until) throw new TimeoutException($"The page never called {identifier}.");
                await Task.Delay(10);
            }
        }

        /// <summary>The reference the page handed arrange.js to call back.</summary>
        public async Task<DotNetObjectReference<ArrangeBlocks>> ArrangeAsync() =>
            Assert.IsType<DotNetObjectReference<ArrangeBlocks>>((await CallAsync("neelamArrange.attach"))[1]);
    }
}
