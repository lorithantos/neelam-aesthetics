namespace Neelam.Campaigns.Tests;

/// <summary>The page reader the page tests use, against the base64 that once failed one.</summary>
public class RenderedPageTests
{
    // An Eastern page as served, with the component state that said "PDT" in a full run
    // (2026-10-09): "...MGxyF+HPDThU7m0g...".
    private const string Eastern =
        "<header><a class=\"site-name\" href=\"/\" data-testid=\"site-name\">Campaign safety</a></header>"
        + "<section class=\"card\"><h2>Announcement</h2>"
        + "<p class=\"muted\" data-testid=\"last-saved-aaaaaaaa-0000-0000-0000-0000000000a1\">Last saved 3 Oct 2026, 8:00 AM EDT</p>"
        + "<a class=\"button\" href=\"templates/aaaaaaaa-0000-0000-0000-0000000000a1\">Edit</a></section>"
        + "<!--Blazor:{\"type\":\"server\",\"descriptor\":\"CfDJ8KmhOHTDkzxBsCmSElS1W4\"}--><!--/bl:20-->"
        + "<script src=\"_framework/blazor.web.js\"></script>"
        + "<!--Blazor-Server-Component-State:CfDJ8MGxyF+HPDThU7m0gQ2rSvFntcEd0R/7xcibH0UrJ3XIynkC-->";

    [Fact]
    public void The_framework_s_base64_is_not_on_the_page()
    {
        // What the old check read, and why it failed.
        Assert.Contains("PDT", Eastern);

        Assert.DoesNotContain("PDT", RenderedPage.Text(Eastern));
        Assert.Equal("Campaign safety Announcement Last saved 3 Oct 2026, 8:00 AM EDT Edit", RenderedPage.Text(Eastern));
    }

    [Fact]
    public void A_named_element_is_read_as_its_text()
    {
        Assert.Equal("Last saved 3 Oct 2026, 8:00 AM EDT",
            RenderedPage.Named(Eastern, "last-saved-aaaaaaaa-0000-0000-0000-0000000000a1"));
        Assert.Equal(["/", "templates/aaaaaaaa-0000-0000-0000-0000000000a1"], RenderedPage.Links(Eastern));
    }

    // A name that is missing, or on two elements, locates nothing: the check fails, it does not guess.
    [Theory]
    [InlineData("last-saved")]
    [InlineData("site")]
    public void A_name_on_no_element_fails(string name) =>
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RenderedPage.Named(Eastern, name));

    [Fact]
    public void A_name_on_two_elements_fails() =>
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            RenderedPage.Named(Eastern + "<p data-testid=\"site-name\">Again</p>", "site-name"));
}
