using System.Net;
using Neelam.Campaigns.Storage;
using Neelam.Web;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// A client's look on its pages, and the Look page where she sets it, requested through the enforcing
/// app. Each test signs in as a member of its own client, so one client's look never meets another test.
/// </summary>
public class LookPagesTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord Blush = Client("blush-salon", 1);
    private static readonly ClientRecord Plain = Client("plain-salon", 2);
    private static readonly ClientRecord Tampered = Client("tampered-salon", 3);
    private static readonly ClientRecord Editing = Client("editing-salon", 4);
    private static readonly ClientRecord Operated = Client("operated-salon", 5);

    private static ClientRecord Client(string name, int n) =>
        new(new ClientName(name), Guid.Parse($"{n:D8}-0000-0000-0000-00000000000{n}"), name);

    private async Task<(HttpStatusCode Status, string Page)> Get(string path, ClientRecord? member, params string[] roles)
    {
        var known = (await app.Clients.ListAsync()).Select(c => c.Name).ToHashSet();
        foreach (var c in new[] { Blush, Plain, Tampered, Editing, Operated }.Where(c => !known.Contains(c.Name)))
            await app.Clients.AddAsync(c);
        var http = app.CreateClient(new() { AllowAutoRedirect = false });
        if (roles.Length > 0) http.SignedIn(roles, member is null ? [] : [member.GroupId]);
        var response = await http.GetAsync(path);
        // Not decoded: the style block is checked exactly as the browser receives it.
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_client_s_pages_carry_its_colours_in_a_style_block()
    {
        await app.Stores.SaveLookAsync(Blush.Name, ClientLookTests.Blush, Actor.Demo);

        var (status, page) = await Get("/templates", Blush, Features.Templates);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(LookStyle.RootBlock(ClientLookTests.Blush), RenderedPage.Named(page, "client-look"));
        // As written, not entity-encoded: inside a style element the browser would not decode it.
        Assert.Contains($"<style data-testid=\"client-look\">{LookStyle.RootBlock(ClientLookTests.Blush)}</style>", page);
    }

    // Nothing of her own: no block, so the stylesheet's defaults, the standard look, stand.
    [Fact]
    public async Task A_client_with_no_look_of_its_own_gets_the_standard_one()
    {
        var (status, page) = await Get("/templates", Plain, Features.Templates);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, RenderedPage.Count(page, "client-look"));
    }

    // A look document changed by hand to carry CSS of its own is refused as it is read: the page
    // still opens, in the standard look, and none of it reaches the page.
    [Fact]
    public async Task A_stored_look_that_is_not_colours_never_reaches_the_page()
    {
        app.Containers.For("settings").Put(
            "tampered-salon/20261003T090000.0000000Z.json",
            """{"schema":2,"pageBackground":"red;}body{display:none"}""");

        var (status, page) = await Get("/templates", Tampered, Features.Templates);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, RenderedPage.Count(page, "client-look"));
        Assert.DoesNotContain("display:none", page);
    }

    // The operator's pages are no client's: they keep the standard look whoever is signed in.
    [Fact]
    public async Task Admin_pages_ignore_the_client_s_look()
    {
        await app.Stores.SaveLookAsync(Operated.Name, ClientLookTests.Blush, Actor.Demo);
        var (_, clientPage) = await Get("/templates", Operated, Features.Templates, Features.Operator);
        Assert.Equal(1, RenderedPage.Count(clientPage, "client-look"));

        var (status, page) = await Get("/admin/clients", Operated, Features.Templates, Features.Operator);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, RenderedPage.Count(page, "client-look"));
    }

    [Fact]
    public async Task Nobody_signed_in_is_challenged_on_the_look_page() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/look", Editing)).Status);

    // The look page is the Client.Look feature's, and no other.
    [Fact]
    public async Task The_look_page_needs_the_look_feature()
    {
        var (status, _) = await Get("/look", Editing, [.. Features.All.Where(f => f != Features.Look)]);

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task The_look_page_starts_from_the_standard_look()
    {
        var (status, page) = await Get("/look", Plain, Features.Look);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("The standard look.", RenderedPage.Named(page, "look-status"));
        Assert.Equal(LookPalette.Standard.PageBackground, RenderedPage.Attribute(page, "look-hex-background", "value"));
        Assert.Equal(LookPalette.Standard.Accent, RenderedPage.Attribute(page, "look-hex-accent", "value"));
        Assert.Equal(0, RenderedPage.Count(page, "look-problems"));
        // Every colour set on the sample, the standard ones too, so the page's own look never shows through.
        Assert.Equal(LookStyle.Declarations(LookPalette.Standard), RenderedPage.Attribute(page, "look-sample", "style"));
    }

    // Her own colours in the fields, and the sample drawn with every one of them.
    [Fact]
    public async Task The_look_page_shows_her_colours_and_a_sample_in_them()
    {
        await app.Stores.SaveLookAsync(Editing.Name, ClientLookTests.Blush, Actor.Demo);

        var (status, page) = await Get("/look", Editing, Features.Look);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("Your own look.", RenderedPage.Named(page, "look-status"));
        Assert.Equal("#faf0f2", RenderedPage.Attribute(page, "look-hex-background", "value"));
        Assert.Equal("#fffbf7", RenderedPage.Attribute(page, "look-hex-surface", "value"));
        Assert.Equal("#e3cfcf", RenderedPage.Attribute(page, "look-hex-border", "value"));
        Assert.Equal("#2d2525", RenderedPage.Attribute(page, "look-hex-text", "value"));
        Assert.Equal("#6b5858", RenderedPage.Attribute(page, "look-hex-muted", "value"));
        Assert.Equal("#225e3e", RenderedPage.Attribute(page, "look-hex-accent", "value"));
        Assert.Equal(LookStyle.Declarations(ClientLookTests.Blush.Palette),
            RenderedPage.Attribute(page, "look-sample", "style"));
    }

    // The header links to it from every client page.
    [Fact]
    public async Task Every_client_page_links_to_the_look_page()
    {
        var (_, page) = await Get("/templates", Plain, Features.Templates);

        Assert.Contains("look", RenderedPage.Links(page));
    }
}
