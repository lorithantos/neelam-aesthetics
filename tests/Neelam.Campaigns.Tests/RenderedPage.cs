using System.Net;
using System.Text.RegularExpressions;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Reads a rendered page the way a check should: by a named element, never by searching the whole
/// HTML for a short string. Blazor writes fresh random base64 into every response, the prerender
/// marker's descriptor and the component state, about 2,000 characters a page, so a short run of
/// letters turns up there by chance: "PDT" did, in 15 of 2,000 requests, and a check that an Eastern
/// page does not say "PDT" failed once in a full run.
///
/// An element a check looks for carries <c>data-testid</c>: kebab-case, unique on its page, and for
/// a repeated element (one per card in a list) suffixed with the id of what it shows, such as
/// <c>last-saved-{id}</c>. A browser-driven suite can locate the same names.
/// </summary>
internal static class RenderedPage
{
    private static readonly Regex Comments = new("<!--.*?-->", RegexOptions.Singleline);
    private static readonly Regex ScriptsAndStyles = new(@"<(script|style)\b.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Tags = new("<[^>]*>");
    private static readonly Regex Space = new(@"\s+");
    private static readonly Regex Hrefs = new("<a\\b[^>]*?\\shref=\"([^\"]*)\"", RegexOptions.IgnoreCase);

    /// <summary>
    /// The text of the one element named <paramref name="name"/>, decoded and with its whitespace
    /// collapsed. Fails unless exactly one element carries the name.
    /// </summary>
    public static string Named(string html, string name)
    {
        var elements = new Regex(
            $"<(?<tag>[a-z][a-z0-9-]*)\\b[^>]*?\\sdata-testid=\"{Regex.Escape(name)}\"[^>]*>(?<inner>.*?)</\\k<tag>>",
            RegexOptions.Singleline).Matches(html);
        if (elements.Count != 1)
            Assert.Fail($"Expected one element with data-testid=\"{name}\", found {elements.Count}.");
        return Readable(elements[0].Groups["inner"].Value);
    }

    /// <summary>How many elements carry the name <paramref name="name"/>: for a check that one is absent.</summary>
    public static int Count(string html, string name) =>
        Regex.Matches(Comments.Replace(html, ""), $"<[a-z][a-z0-9-]*\\b[^>]*?\\sdata-testid=\"{Regex.Escape(name)}\"").Count;

    /// <summary>
    /// The rest of every name that starts with <paramref name="prefix"/>, in the order the elements
    /// come on the page: for a check on the order of a list whose items are named by id, such as
    /// <c>earlier-look-when-{id}</c>.
    /// </summary>
    public static IReadOnlyList<string> NamesStartingWith(string html, string prefix) =>
        Regex.Matches(Comments.Replace(html, ""), $"<[a-z][a-z0-9-]*\\b[^>]*?\\sdata-testid=\"{Regex.Escape(prefix)}([^\"]*)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();

    /// <summary>
    /// The value of <paramref name="attribute"/> on the one element named <paramref name="name"/>,
    /// decoded: for a field, whose value is an attribute rather than text. Fails unless exactly one
    /// element carries the name and it has the attribute.
    /// </summary>
    public static string Attribute(string html, string name, string attribute)
    {
        var tags = Regex.Matches(html, $"<[a-z][a-z0-9-]*\\b[^>]*?\\sdata-testid=\"{Regex.Escape(name)}\"[^>]*>");
        if (tags.Count != 1)
            Assert.Fail($"Expected one element with data-testid=\"{name}\", found {tags.Count}.");
        var value = Regex.Match(tags[0].Value, $"\\s{Regex.Escape(attribute)}=\"([^\"]*)\"");
        if (!value.Success)
            Assert.Fail($"The element with data-testid=\"{name}\" has no {attribute}.");
        return WebUtility.HtmlDecode(value.Groups[1].Value);
    }

    /// <summary>
    /// What a reader sees on the whole page: its text, without comments (where Blazor keeps its
    /// state), scripts, styles, tags or attributes. For a check that something is nowhere on the
    /// page, which no one element can answer.
    /// </summary>
    public static string Text(string html) => Readable(ScriptsAndStyles.Replace(Comments.Replace(html, ""), " "));

    /// <summary>Every link's address on the page.</summary>
    public static IReadOnlyList<string> Links(string html) =>
        Hrefs.Matches(Comments.Replace(html, "")).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();

    private static string Readable(string html) =>
        Space.Replace(WebUtility.HtmlDecode(Tags.Replace(html, " ")), " ").Trim();
}
