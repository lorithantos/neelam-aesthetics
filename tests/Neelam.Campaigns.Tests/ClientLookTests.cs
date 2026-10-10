using System.Text.RegularExpressions;
using Neelam.Campaigns.Storage;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// A client's look as data (owner, 2026-10-09: "maybe a light pink background and something softer
/// than white for the main editing"): its colours, the style block they become, the contrast they
/// must keep, and the stylesheet whose defaults they stand in for.
/// </summary>
public class ClientLookTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    // The look proposed for Neelam, for the owner to approve; not saved anywhere live.
    internal static readonly ClientLook Blush = new(
        accentColour: "#225e3e", pageBackground: "#faf0f2", surface: "#fffbf7", border: "#e3cfcf", text: "#2d2525",
        mutedText: "#6b5858");

    private static readonly string Stylesheet =
        File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web", "wwwroot", "app.css"));

    [Fact]
    public void The_style_block_carries_the_client_s_colours() =>
        Assert.Equal(
            ":root{--bg:#faf0f2;--surface:#fffbf7;--border:#e3cfcf;--text:#2d2525;--text-muted:#6b5858;" +
            "--accent:#225e3e;--accent-hover:#1b4b31}",
            LookStyle.RootBlock(Blush));

    // Only what the look sets is written; the stylesheet's own defaults stand for the rest.
    [Fact]
    public void Missing_colours_fall_back_to_the_defaults()
    {
        Assert.Equal(":root{--bg:#faf0f2}", LookStyle.RootBlock(new ClientLook(pageBackground: "#faf0f2")));
        Assert.Null(LookStyle.RootBlock(ClientLook.Default));

        var palette = new ClientLook(pageBackground: "#faf0f2").Palette;
        Assert.Equal(LookPalette.Standard with { PageBackground = "#faf0f2" }, palette);
    }

    // Each colour is written into the pages' style, so anything but # and six hex digits is refused
    // before it can be held: a value that closes the block and opens another, a named colour, a
    // short form, or a colour with a newline after it.
    [Theory]
    [InlineData("accent", "red;}body{display:none")]
    [InlineData("background", "red;}body{display:none")]
    [InlineData("surface", "#fffbf7;}body{display:none")]
    [InlineData("border", "</style><script>alert(1)</script>")]
    [InlineData("text", "red")]
    [InlineData("muted", "#fff")]
    [InlineData("background", "#faf0f2\n")]
    [InlineData("surface", " #fffbf7")]
    public void Only_a_hex_colour_is_held(string colour, string value) =>
        Assert.Throws<ArgumentException>(() => colour switch
        {
            "accent" => new ClientLook(accentColour: value),
            "background" => new ClientLook(pageBackground: value),
            "surface" => new ClientLook(surface: value),
            "border" => new ClientLook(border: value),
            "text" => new ClientLook(text: value),
            "muted" => new ClientLook(mutedText: value),
            _ => throw new ArgumentOutOfRangeException(nameof(colour)),
        });

    // A document changed by hand is refused as it is read, so it never reaches a page.
    [Fact]
    public void A_stored_look_holding_anything_but_colours_is_refused() =>
        Assert.Throws<ArgumentException>(() => CampaignJson.DeserializeLook(
            """{"schema":2,"accentColour":"#225e3e","pageBackground":"red;}body{display:none"}"""));

    // Saved when the accent was the only colour: it reads the same, every other colour the standard one.
    [Fact]
    public void An_old_look_reads_unchanged()
    {
        var look = CampaignJson.DeserializeLook("""{"schema":2,"accentColour":"#B08D57"}""");

        Assert.Equal(new ClientLook("#B08D57"), look);
        Assert.Equal(LookPalette.Standard with { Accent = "#B08D57" }, look.Palette);
        Assert.Equal(":root{--accent:#B08D57;--accent-hover:#8c7045}", LookStyle.RootBlock(look));
    }

    [Fact]
    public void Every_colour_round_trips_as_json() =>
        Assert.Equal(Blush, CampaignJson.DeserializeLook(CampaignJson.SerializeLook(Blush)));

    [Theory]
    [InlineData("#000000", "#ffffff", 21.0)]
    [InlineData("#225e3e", "#225e3e", 1.0)]
    [InlineData("#777777", "#ffffff", 4.48)]
    [InlineData("#2d2525", "#faf0f2", 13.41)]
    public void Contrast_is_wcag_s(string foreground, string background, double ratio) =>
        Assert.Equal(ratio, Contrast.Ratio(foreground, background), precision: 2);

    [Fact]
    public void The_proposed_blush_look_passes_aa_on_every_pair()
    {
        Assert.Empty(Blush.Palette.ContrastProblems());
        Assert.All(Blush.Palette.TextPairs, p => Assert.True(p.Ratio >= 4.5, $"{p.Name}: {p.Ratio:0.00}"));
    }

    // Each pair she reads text in is checked, and a failing one is named with its ratio.
    [Theory]
    [InlineData("text", "#bbbbbb", "Text on the page background is 1.79:1; it needs at least 4.5:1.")]
    [InlineData("text", "#bbbbbb", "Text on the editing surface is 1.91:1; it needs at least 4.5:1.")]
    [InlineData("muted", "#999999", "Muted text on the editing surface is 2.84:1; it needs at least 4.5:1.")]
    // Just under AA: the ratio is cut, never rounded up to look like a pass.
    [InlineData("muted", "#777777", "Muted text on the editing surface is 4.47:1; it needs at least 4.5:1.")]
    [InlineData("accent", "#ffcc00", "Links in the accent colour on the page background is")]
    [InlineData("accent", "#ffcc00", "Button text on the accent colour is 1.51:1; it needs at least 4.5:1.")]
    [InlineData("surface", "#1d2127", "Text on the editing surface is 1.00:1")]
    public void A_look_too_faint_to_read_is_refused_naming_the_pair(string colour, string value, string named)
    {
        var look = colour switch
        {
            "text" => new ClientLook(text: value),
            "muted" => new ClientLook(mutedText: value),
            "accent" => new ClientLook(accentColour: value),
            "surface" => new ClientLook(surface: value),
            _ => throw new ArgumentOutOfRangeException(nameof(colour)),
        };

        Assert.Contains(look.Palette.ContrastProblems(), p => p.StartsWith(named, StringComparison.Ordinal));
    }

    // The store is the guard of record: what the page shows is the same check, but a save that gets
    // past the page is still refused, and nothing is kept.
    [Fact]
    public async Task Saving_a_failing_look_is_refused_and_nothing_is_kept()
    {
        var client = new ClientName("faint-salon");

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            app.Stores.SaveLookAsync(client, Blush.WithText("#d0c4c4"), Actor.Demo));

        Assert.Contains("Text on the page background is", refused.Message);
        Assert.Null(await app.Stores.Look(client).CurrentAsync());
        Assert.DoesNotContain(app.Activity.Events, e => e.Client == client);
    }

    [Fact]
    public async Task Saving_and_going_back_to_the_standard_look_are_on_the_trail_by_id_only()
    {
        var client = new ClientName("blush-salon");

        await app.Stores.SaveLookAsync(client, Blush, Actor.Demo);
        Assert.Equal(Blush, await app.Stores.Look(client).CurrentAsync());

        // Back to the standard look by a version that says so: her own is kept, to use again.
        app.Clock.Now += TimeSpan.FromMinutes(1);
        await app.Stores.UseStandardLookAsync(client, Actor.Demo);
        Assert.Equal(ClientLook.Standard, await app.Stores.Look(client).CurrentAsync());
        Assert.Equal(2, app.Containers.For("settings").Blobs.Keys.Count(k => k.StartsWith("blush-salon/", StringComparison.Ordinal)));

        var events = app.Activity.Events.Where(e => e.Client == client).ToList();
        Assert.Equal([ActivityAction.LookSaved, ActivityAction.LookResetToStandard], events.Select(e => e.Action));
        Assert.All(events, e => Assert.Equal((ActivityEntity.Look, "look"), (e.Entity, e.EntityId)));
        Assert.All(events, e => Assert.NotNull(e.SaveStamp));
    }

    // The contrast check reads the standard look from code; the pages draw it from the stylesheet.
    // They are one look only while the two agree.
    [Fact]
    public void The_stylesheet_s_defaults_are_the_standard_look()
    {
        var defaults = Defaults();
        var standard = LookPalette.Standard;

        Assert.Equal(standard.PageBackground, defaults["--bg"]);
        Assert.Equal(standard.Surface, defaults["--surface"]);
        Assert.Equal(standard.Border, defaults["--border"]);
        Assert.Equal(standard.Text, defaults["--text"]);
        Assert.Equal(standard.MutedText, defaults["--text-muted"]);
        Assert.Equal(standard.Accent, defaults["--accent"]);
        Assert.Equal(LookPalette.AccentText, defaults["--accent-text"]);
    }

    // Every property a look sets is one the stylesheet defines, so each colour she picks lands somewhere.
    [Fact]
    public void Every_property_a_look_sets_is_defined_in_the_stylesheet()
    {
        var set = Regex.Matches(LookStyle.RootBlock(Blush)!, "(--[a-z-]+):").Select(m => m.Groups[1].Value);

        Assert.All(set, property => Assert.Contains(property, Defaults().Keys));
    }

    // A look changes the custom properties, so a rule naming a colour of its own would not follow it.
    [Fact]
    public void No_rule_names_a_colour_of_its_own()
    {
        var rules = Regex.Replace(Stylesheet[(RootBlock().Index + RootBlock().Length)..], @"/\*.*?\*/", "", RegexOptions.Singleline);
        rules = Regex.Replace(rules, @"url\([^)]*\)", "url()");
        var colour = new Regex(
            @"#[0-9a-fA-F]{3,8}\b|\b(rgba?|hsla?|hwb|lab|lch|oklab|oklch)\(|:\s*[^;{}]*\b(white|black|red|green|blue|yellow|lightyellow|pink|gray|grey|silver|orange|purple|navy|maroon)\b",
            RegexOptions.IgnoreCase);

        Assert.Empty(colour.Matches(rules).Select(m => m.Value));
    }

    private static Match RootBlock() => Regex.Match(Stylesheet, @":root\s*\{(?<body>[^}]*)\}");

    private static Dictionary<string, string> Defaults() =>
        Regex.Matches(RootBlock().Groups["body"].Value, @"(?<name>--[a-z-]+)\s*:\s*(?<value>[^;]+);")
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value.Trim());
}

internal static class LookChanges
{
    /// <summary>The look with its text in <paramref name="text"/>, the rest as it was.</summary>
    public static ClientLook WithText(this ClientLook look, string text) =>
        new(look.AccentColour, look.PageBackground, look.Surface, look.Border, text, look.MutedText);
}
