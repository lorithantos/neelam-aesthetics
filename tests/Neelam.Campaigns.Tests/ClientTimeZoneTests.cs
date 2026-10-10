using System.Net;
using Neelam.Campaigns.Storage;
using Neelam.Web.Components.Pages;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// A client's time zone is registration data (owner, 2026-10-09: "Pacific is fine as a default, but the
/// setting should be in the client metadata"): a <c>TimeZone</c> on its clients-table row, an IANA id,
/// left out for the default. Which zone a page uses is in <see cref="ClientWorkspaceTests"/>.
/// </summary>
public class ClientTimeZoneTests
{
    private static readonly ClientRecord Salon = new(
        new ClientName("test-salon-one"), Guid.Parse("44444444-4444-4444-4444-444444444444"), "Salon One");

    [Fact]
    public void The_clients_table_keeps_the_zone_and_reads_it_back()
    {
        var registered = Salon with { TimeZone = "America/New_York" };

        var row = TableMetadata.FromClient(registered);

        Assert.Equal("America/New_York", row.GetString("TimeZone"));
        Assert.Equal(registered, TableMetadata.ToClient(row));
    }

    // Rows written before zones were registered have no TimeZone property at all, and read as the default.
    [Fact]
    public void A_row_with_no_zone_is_left_without_one_and_reads_as_none()
    {
        var row = TableMetadata.FromClient(Salon);
        Assert.False(row.ContainsKey("TimeZone"));

        Assert.Null(TableMetadata.ToClient(row).TimeZone);
    }

    // A stored zone is read as written, known or not: refusing it here would take every page down
    // with it, so the pages fall back to the default instead.
    [Fact]
    public void A_stored_zone_this_machine_does_not_know_still_reads()
    {
        var row = TableMetadata.FromClient(Salon);
        row["TimeZone"] = "Mars/Olympus_Mons";

        Assert.Equal("Mars/Olympus_Mons", TableMetadata.ToClient(row).TimeZone);
    }

    private static ClientForm FormWithZone(string zone)
    {
        var form = ClientForm.Of(Salon);
        form.TimeZone = zone;
        return form;
    }

    [Fact]
    public void The_admin_form_round_trips_the_zone_and_blank_is_the_default()
    {
        var (client, errors) = FormWithZone(" America/Chicago ").ToRecord();

        Assert.Empty(errors);
        Assert.Equal("America/Chicago", client!.TimeZone);
        Assert.Equal("America/Chicago", ClientForm.Of(client).TimeZone);
        Assert.Null(FormWithZone("  ").ToRecord().Client!.TimeZone);
    }

    [Fact]
    public void The_admin_form_refuses_a_zone_it_does_not_know_naming_it()
    {
        var (client, errors) = FormWithZone("Pacific Time").ToRecord();

        Assert.Null(client);
        Assert.Equal(
            "\"Pacific Time\" is not a time zone: give its IANA name, such as America/Los_Angeles or America/New_York.",
            Assert.Single(errors));
    }

    // The table's own rules refuse it too, whatever page or tool writes the row, on add and on update.
    [Fact]
    public async Task The_clients_table_refuses_a_zone_it_does_not_know_on_add_and_update()
    {
        var clients = new InMemoryClientDirectory();

        var added = await Assert.ThrowsAsync<ArgumentException>(
            () => clients.AddAsync(Salon with { TimeZone = "Mars/Olympus_Mons" }));
        await clients.AddAsync(Salon with { TimeZone = "America/New_York" });
        var updated = await Assert.ThrowsAsync<ArgumentException>(
            () => clients.UpdateAsync(Salon with { TimeZone = "Mars/Olympus_Mons" }));

        Assert.Contains("\"Mars/Olympus_Mons\" is not a time zone", added.Message);
        Assert.Contains("\"Mars/Olympus_Mons\" is not a time zone", updated.Message);
        Assert.Equal("America/New_York", Assert.Single(await clients.ListAsync()).TimeZone);
    }
}

/// <summary>Through the enforcing app: each client's pages show its times in its own zone.</summary>
public class ClientTimeZonePageTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord NewYork = new(
        new ClientName("test-salon-one"), Guid.Parse("55555555-5555-5555-5555-555555555551"), "Salon One")
    {
        TimeZone = "America/New_York",
    };

    // No zone registered: the default, Pacific.
    private static readonly ClientRecord Unzoned = new(
        new ClientName("test-salon-two"), Guid.Parse("55555555-5555-5555-5555-555555555552"), "Salon Two");

    private static readonly Guid Template = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1");
    private static readonly Guid Campaign = Guid.Parse("cccccccc-0000-0000-0000-0000000000c1");

    private async Task<string> Get(string path, string role, ClientRecord member)
    {
        if ((await app.Clients.ListAsync()).Count == 0)
        {
            await app.Clients.AddAsync(NewYork);
            await app.Clients.AddAsync(Unzoned);
            // Each client's own template and campaign, all saved at the app's clock, 12:00 UTC on 3 October 2026.
            foreach (var client in new[] { NewYork, Unzoned })
            {
                var store = app.Stores.Campaigns(client.Name, Actor.Demo);
                await store.SaveTemplateAsync(Template, RegisteredPhoneTests.SignedOffWith("Snohomish, WA"));
                await store.SaveDraftAsync(Campaign, "Open house", new CampaignDraft(null, []));
            }
        }
        var http = app.CreateClient(new() { AllowAutoRedirect = false });
        http.SignedIn([role], [member.GroupId]);
        var response = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Two_clients_each_see_their_templates_saved_in_their_own_zone()
    {
        var eastern = await Get("/templates", Features.Templates, NewYork);
        var pacific = await Get("/templates", Features.Templates, Unzoned);

        Assert.Equal("Last saved 3 Oct 2026, 8:00 AM EDT", RenderedPage.Named(eastern, $"last-saved-{Template}"));
        Assert.Equal("Last saved 3 Oct 2026, 5:00 AM PDT", RenderedPage.Named(pacific, $"last-saved-{Template}"));
        // And no other time on either page in the other zone. On the text a reader sees: the raw
        // page's random base64 said "PDT" in 15 of 2,000 requests.
        Assert.DoesNotContain("PDT", RenderedPage.Text(eastern));
        Assert.DoesNotContain("EDT", RenderedPage.Text(pacific));
    }

    // Every other page that shows a time, in the zone of the client whose page it is. The editors
    // end the line with a full stop; the list does not.
    [Theory]
    [InlineData("/templates/aaaaaaaa-0000-0000-0000-0000000000a1", Features.Templates, "last-saved", ".")]
    [InlineData("/campaigns", Features.Campaigns, "last-saved-cccccccc-0000-0000-0000-0000000000c1", "")]
    [InlineData("/campaigns/cccccccc-0000-0000-0000-0000000000c1", Features.Campaigns, "last-saved", ".")]
    public async Task Each_page_shows_the_last_save_in_the_client_s_zone(string path, string role, string element, string end)
    {
        Assert.Equal("Last saved 3 Oct 2026, 8:00 AM EDT" + end, RenderedPage.Named(await Get(path, role, NewYork), element));
        Assert.Equal("Last saved 3 Oct 2026, 5:00 AM PDT" + end, RenderedPage.Named(await Get(path, role, Unzoned), element));
    }

    [Fact]
    public async Task The_operator_sees_each_client_s_zone_and_the_default()
    {
        var page = await Get("/admin/clients", Features.Operator, NewYork);

        Assert.Contains("Times shown in America/New_York.", page);
        Assert.Contains("Times shown in America/Los_Angeles, the default.", page);
    }
}
