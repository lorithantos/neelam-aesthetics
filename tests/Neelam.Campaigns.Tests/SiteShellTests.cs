using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Neelam.Web.Components.Shared;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// What every page shares: the site's own files at fingerprinted addresses, the deploy's build stamp
/// in the footer, and a home page that says what the site does here.
/// </summary>
public class SiteShellTests(DemoApp app) : IClassFixture<DemoApp>
{
    private const string Stamp = "Build 73 (local) | Commit ae0d72c1a2b3 | Branch main | 2026-10-09T18:00:00Z";

    private static async Task<string> Get(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private Task<string> Get(string path) => Get(app.CreateClient(new() { AllowAutoRedirect = false }), path);

    // A browser that kept an older app.css once showed the error bar on a newer page. The page now
    // links the file by an address that changes with its content (MapStaticAssets), and the app
    // serves that address. A published build serves it with long-lived "immutable" caching; the
    // tests run on the development manifest, which asks for revalidation instead, so that header is
    // not pinned here.
    [Theory]
    [InlineData("app", "css", "<link rel=\"stylesheet\" href=\"", "/* Campaign safety: hand-written styles")]
    [InlineData("copy", "js", "<script src=\"", "data-copy")]
    public async Task The_site_s_own_files_are_linked_by_fingerprint(string name, string extension, string tag, string content)
    {
        var page = await Get("/");

        var linked = Regex.Match(page, $"{Regex.Escape(tag)}({name}\\.[a-z0-9]{{6,}}\\.{extension})\"");
        Assert.True(linked.Success, $"No fingerprinted {name}.{extension} is linked.");
        Assert.DoesNotContain($"{tag}{name}.{extension}\"", page);

        var file = await app.CreateClient().GetAsync(linked.Groups[1].Value);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Contains(content, await file.Content.ReadAsStringAsync());
    }

    // Static files moved from UseStaticFiles to MapStaticAssets; Blazor's own script must still load,
    // or no interactive page works.
    [Fact]
    public async Task Blazor_s_script_is_still_served()
    {
        Assert.Contains("<script src=\"_framework/blazor.web.js\"></script>", await Get("/"));
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/_framework/blazor.web.js")).StatusCode);
    }

    // The deploy writes its stamp to the App Service setting LATEST_BUILD_INFO; every page shows it
    // quietly at the foot. (The admin layout's, in Enforced below: the demo has no admin pages.)
    [Theory]
    [InlineData("/")]
    [InlineData("/campaigns")]
    public async Task Every_page_shows_the_deploy_s_build_stamp_in_its_footer(string path)
    {
        using var stamped = app.WithWebHostBuilder(builder => builder.UseSetting(BuildStamp.Setting, Stamp));

        var page = await Get(stamped.CreateClient(new() { AllowAutoRedirect = false }), path);

        Assert.Contains($"<footer class=\"site-footer\"><span class=\"build-stamp\">{Stamp}</span></footer>", page);
    }

    // A run the deploy never stamped, such as a local one, has no footer at all.
    [Fact]
    public async Task Without_a_stamp_there_is_no_footer() =>
        Assert.DoesNotContain("site-footer", await Get("/"));

    // On the demo every step works, so the home page must not say nothing can be shown or changed.
    [Fact]
    public async Task The_demo_home_page_says_what_she_can_do()
    {
        var page = Regex.Replace(await Get("/"), "\\s+", " ");

        Assert.DoesNotContain("does not show or change any campaigns", page);
        Assert.Contains("Write a campaign from one of your templates, see the checks as you go, approve it, and copy it into Square.", page);
        Assert.Contains("Start in <a href=\"campaigns\">Campaigns</a>", page);
    }
}

/// <summary>Enforced, as production runs before sign-in exists: the home page says nothing opens yet.</summary>
public class EnforcedHomeTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    [Fact]
    public async Task The_home_page_says_sign_in_is_not_set_up()
    {
        var page = await app.CreateClient().GetStringAsync("/");

        Assert.Contains("Sign-in is not set up yet, so this app does not show or change any campaigns.", page);
        Assert.DoesNotContain("Write a campaign from one of your templates", page);
    }

    [Fact]
    public async Task The_admin_layout_shows_the_build_stamp_too()
    {
        const string stamp = "Build 73 (local) | Commit ae0d72c1a2b3 | Branch main | 2026-10-09T18:00:00Z";
        using var stamped = app.WithWebHostBuilder(builder => builder.UseSetting(BuildStamp.Setting, stamp));
        var client = stamped.CreateClient(new() { AllowAutoRedirect = false });
        client.SignedIn([Neelam.Web.Security.Features.Operator], []);

        var response = await client.GetAsync("/admin/clients");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"<footer class=\"site-footer\"><span class=\"build-stamp\">{stamp}</span></footer>",
            WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }
}
