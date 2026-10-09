using System.Net;
using System.Text.RegularExpressions;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The image library page, and photos in the template and campaign previews, requested through the
/// enforcing app. Salon one's library holds the Beauty Bank photos as Square-hosted entries; salon
/// two has the same template and campaign and an empty library.
/// </summary>
public class ImagePagesTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord SalonOne = new(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One");

    private static readonly ClientRecord SalonTwo = new(
        new ClientName("test-salon-two"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "Salon Two");

    private static readonly Guid Membership = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Finished = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // The template fixes "Principals toasting" in its header; the campaign fills in "Principals seated".
    private const string Toasting =
        "https://postoffice-production-f.squarecdn.com/images/principals-toasting.jpg?enable=upscale&height=196&width=640";
    private const string Seated =
        "https://square-web-production-f.squarecdn.com/files/seated/original.jpeg?crop=1:1";

    private static readonly string[] Everything = [Features.Campaigns, Features.Templates];

    private async Task<(HttpStatusCode Status, string Page)> Get(string path, string[] roles, Guid[]? groups = null)
    {
        if ((await app.Clients.ListAsync()).Count == 0)
        {
            await app.Clients.AddAsync(SalonOne);
            await app.Clients.AddAsync(SalonTwo);
            foreach (var salon in new[] { SalonOne, SalonTwo })
            {
                var store = app.Stores.Campaigns(salon.Name, Actor.Demo);
                await store.SaveTemplateAsync(Membership, DraftFixtures.Membership);
                await store.SaveDraftAsync(Finished, "WE’RE TURNING ONE!", DraftFixtures.Finished());
            }
            var images = app.Stores.Images(SalonOne.Name, Actor.Demo);
            await images.AddFromSquareAsync("Principals toasting", Toasting);
            await images.AddFromSquareAsync("Principals seated", Seated);
        }
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        if (roles.Length > 0) client.SignedIn(roles, groups ?? [SalonOne.GroupId]);
        var response = await client.GetAsync(path);
        // Razor encodes & in an address, and characters outside ASCII.
        return (response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    private static string Shown(string address) => $"<img class=\"preview-image\" src=\"{address}\"";

    // The note under the preview's photo, outside the email: its name, then the plain message.
    private static string Missing(string name) =>
        $"<p class=\"field-help\" data-photo-note>For you, not in the email: the photo \"{Regex.Escape(name)}\"\\. {Regex.Escape(ImageLibrary.NotInLibrary)}</p>";

    [Fact]
    public async Task Nobody_signed_in_is_challenged() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/images", [])).Status);

    [Fact]
    public async Task Changing_templates_alone_does_not_open_the_library() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/images", [Features.Templates])).Status);

    // A Square photo is shown from Square's own address, with Square's crop and size kept.
    [Fact]
    public async Task Template_and_campaign_previews_show_a_Square_photo_from_its_address()
    {
        var (_, template) = await Get($"/templates/{Membership}", Everything);
        var (_, campaign) = await Get($"/campaigns/{Finished}", Everything);

        Assert.Contains(Shown(Toasting), template);
        Assert.Contains(Shown(Toasting), campaign);
        Assert.Contains(Shown(Seated), campaign);
        // Both agree the photo is there.
        Assert.DoesNotContain(ImageLibrary.NotInLibrary, template);
        Assert.DoesNotContain(ImageLibrary.NotInLibrary, campaign);
    }

    // The mismatch that was reported: the template page said the photo was not in the library while
    // the campaign preview named it as if it were. For a name the library does not hold, both now say
    // the same thing. Salon two's library is empty, and salon one's photos are not hers.
    [Fact]
    public async Task Template_and_campaign_agree_on_a_photo_the_library_does_not_hold()
    {
        var (_, template) = await Get($"/templates/{Membership}", Everything, [SalonTwo.GroupId]);
        var (_, campaign) = await Get($"/campaigns/{Finished}", Everything, [SalonTwo.GroupId]);

        Assert.Matches(Missing("Principals toasting"), template);
        Assert.Matches(Missing("Principals toasting"), campaign);
        Assert.Matches(Missing("Principals seated"), campaign);
        foreach (var page in new[] { template, campaign })
        {
            Assert.DoesNotContain("preview-image", page);
            Assert.DoesNotContain("squarecdn.com", page);
        }
    }

    [Fact]
    public async Task The_library_lists_her_photos_and_offers_to_add_from_Square()
    {
        var (status, page) = await Get("/images", [Features.Campaigns]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("<h2>Principals seated</h2>", page);
        Assert.Contains("<h2>Principals toasting</h2>", page);
        Assert.Contains($"src=\"{Toasting}\"", page);
        Assert.Contains("Photos for Salon One. A photo block in a template or campaign names one of these by its name.", page);
        Assert.DoesNotContain("Salon One's", page);
        Assert.Contains("id=\"square-name\"", page);
        Assert.Contains("id=\"square-url\"", page);
        Assert.Contains("Add from Square</button>", page);
        Assert.Contains("deleting it later removes only that, never anything at Square", Regex.Replace(page, "\\s+", " "));
    }

    [Fact]
    public async Task Another_client_s_photos_are_never_shown()
    {
        var (status, page) = await Get("/images", [Features.Campaigns], [SalonTwo.GroupId]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Photos for Salon Two.", page);
        Assert.Contains("No photos yet.", page);
        Assert.DoesNotContain("<h2>Principals", page);
        Assert.DoesNotContain("principals-toasting", page);
        Assert.DoesNotContain("files/seated", page);
    }

    // Her photo and its name are what she needs; Square's long address waits behind a disclosure.
    // Delete sits beside the name, not under the image, where its confirmation then opens.
    [Fact]
    public async Task Each_photo_has_its_address_behind_a_disclosure_and_Delete_beside_its_name()
    {
        var (_, page) = await Get("/images", [Features.Campaigns]);

        var card = Regex.Match(page, "<section class=\"card\" aria-label=\"Principals toasting\">(.*?)</section>", RegexOptions.Singleline).Value;
        Assert.Matches(
            "<div class=\"library-entry-head\">\\s*<h2>Principals toasting</h2>\\s*<button class=\"button\"[^>]*>Delete</button>\\s*</div>",
            card);
        Assert.Matches(
            $"<details class=\"library-address\">\\s*<summary>Square address</summary>\\s*<span>{Regex.Escape(Toasting)}</span>\\s*</details>",
            card);
        // The address appears once, in the disclosure, besides the image's own src.
        Assert.Equal(2, Regex.Matches(card, Regex.Escape(Toasting)).Count);
        Assert.True(card.IndexOf(">Delete</button>", StringComparison.Ordinal) < card.IndexOf("<img", StringComparison.Ordinal));
    }

    // An entry whose stored address is not on Square's hosts (changed outside the library) is
    // listed so she can delete it, and is never loaded as an image.
    [Fact]
    public async Task An_entry_not_on_Square_is_listed_to_remove_and_never_shown()
    {
        await Get("/images", [Features.Campaigns]);
        app.Containers.For(SalonOne.Name.ToString()).Put("images/Front%20desk", "", new Dictionary<string, string>
        {
            ["name"] = "Front%20desk", ["added"] = "20261003T090000.0000000Z",
            ["square"] = Uri.EscapeDataString("https://example.com/front-desk.jpg"), ["entry"] = Guid.NewGuid().ToString("N"),
        });
        try
        {
            var (_, page) = await Get("/images", [Features.Campaigns]);

            var card = Regex.Match(page, "<section class=\"card\" aria-label=\"Front desk\">(.*?)</section>", RegexOptions.Singleline).Value;
            Assert.Contains(Neelam.Web.Components.Pages.Images.NotASquareAddress, card);
            Assert.Contains(">Delete</button>", card);
            Assert.DoesNotContain("<img", card);
            Assert.DoesNotContain("example.com", page);
        }
        finally
        {
            app.Containers.For(SalonOne.Name.ToString()).Blobs.Remove("images/Front%20desk");
        }
    }

    [Fact]
    public async Task The_client_header_leads_to_the_library()
    {
        var (_, page) = await Get("/images", [Features.Campaigns]);

        var nav = Regex.Match(page, "<nav class=\"site-nav\".*?</nav>", RegexOptions.Singleline).Value;
        Assert.Contains("href=\"images\"", nav);
    }
}
