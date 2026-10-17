using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// "LLM", not "AI", wherever a client reads it, explained a tap away (owner, 2026-10-10: "I prefer llm
/// to ai"; "Having a tooltip is great; it lets her discover any time she stumbles across it"). The
/// explanation is the glossary's, data read and checked at startup.
/// </summary>
public class GlossaryTests
{
    // The owner's words for the explanation, 2026-10-10, as revised: "you don't have to add my
    // opinion, and you can say AI in the explanation".
    private const string OwnersWords =
        "LLM -- a large language model, the kind of AI behind ChatGPT and Claude. Here it reads each saved version the way a careful editor might and points out what looks off. It can be wrong, so check what it says against your offer.";

    [Fact]
    public void The_shipped_glossary_explains_llm_in_the_owner_s_words() =>
        Assert.Equal(OwnersWords, Glossary.Shipped(AppContext.BaseDirectory).Explain(Glossary.Llm));

    [Theory]
    [InlineData("""{ "terms": [] }""", "does not explain 'LLM'")]
    [InlineData("""{ "terms": [ { "term": "LLM", "explanation": "  " } ] }""", "the term 'LLM' has no explanation")]
    [InlineData("""{ "terms": [ { "term": "LLM", "explanation": "One." }, { "term": "LLM", "explanation": "Two." } ] }""", "explains the term 'LLM' twice")]
    [InlineData("""{ "terms": [ { "term": "", "explanation": "One." } ] }""", "has an entry with no term")]
    [InlineData("""{ "terms": [ { "term": "LLM", "explanation": "One.", "tooltip": "x" } ] }""", "glossary.json is not valid")]
    [InlineData("""{ "words": [] }""", "glossary.json is not valid")]
    [InlineData("""{ }""", "has no \"terms\" list")]
    [InlineData("not json", "glossary.json is not valid")]
    public void A_glossary_that_does_not_match_the_code_stops_the_app(string json, string problem)
    {
        var refused = Assert.Throws<GlossaryException>(() => Glossary.Load(json, Glossary.Explained));

        Assert.Contains(refused.Problems, p => p.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_file_is_refused_by_name()
    {
        var nowhere = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var refused = Assert.Throws<GlossaryException>(() => Glossary.Shipped(nowhere));

        Assert.Contains("Catalog/glossary.json is missing", refused.Message);
    }
}

/// <summary>
/// Where "LLM" is explained on the demo with the proofread off, as the test site runs until the key is
/// set: the banner, How it works, the campaign page's checks, and the export's label. Each is a button
/// opening the one explanation on the page, which a screen reader reads as its description.
/// </summary>
public class LlmTermOffTests(DemoApp app) : IClassFixture<DemoApp>
{
    private async Task<string> Get(string path)
    {
        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // The explanation as the shipped file says it, read from the repository, not from the app.
    internal static string FromTheFile() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Campaigns", "Catalog", "glossary.json")))!
            ["terms"]!.AsArray().Single(t => (string?)t!["term"] == "LLM")!["explanation"]!.GetValue<string>();

    /// <summary>
    /// A named element's text as a reader has it: <see cref="RenderedPage"/> puts a space where each tag
    /// was, so a link or "LLM" just before a comma or full stop reads "Campaigns ." there.
    /// </summary>
    internal static string Prose(string text) => Regex.Replace(text, @" (?=[,.:;])", "");

    internal static void Explains(string page, string name)
    {
        Assert.Equal("LLM", RenderedPage.Named(page, name));
        Assert.Equal("llm-explained", RenderedPage.Attribute(page, name, "popovertarget"));
        Assert.Equal("llm-explained-text", RenderedPage.Attribute(page, name, "aria-describedby"));
        Assert.Equal("button", RenderedPage.Attribute(page, name, "type"));
        // What it opens is on the page, the file's words, read by its description's id.
        Assert.Equal(FromTheFile(), RenderedPage.Named(page, "llm-explained"));
        Assert.Equal("llm-explained-text", RenderedPage.Attribute(page, "llm-explained", "id"));
    }

    [Theory]
    [InlineData("/", "llm-term-banner")]
    [InlineData("/campaigns", "llm-term-banner")]
    [InlineData("/how-it-works", "llm-term-banner")]
    [InlineData("/how-it-works", "llm-term-how")]
    public async Task Llm_is_explained_where_it_is_placed(string path, string name) =>
        Explains(await Get(path), name);

    [Fact]
    public async Task The_campaign_page_explains_it_in_the_checks_and_the_export_label_once()
    {
        var store = app.Stores.Campaigns(DemoApp.Client, Actor.Demo);
        var save = await store.SaveDraftAsync(Guid.NewGuid(), "Hello", DraftFixtures.Finished());
        await store.WarningsSeenAtExportAsync(await store.ApproveAsync(save, "Priya"), SeenAtExport.KeysOf(DraftFixtures.Finished()));

        var page = await Get($"/campaigns/{save.Id}");

        Explains(page, "llm-term-checks");
        Explains(page, "llm-term-export");
        Assert.Equal("These are the instant rules. Proofreading with the LLM is not switched on yet.", RenderedPage.Named(page, "checks-llm-off"));
        Assert.Equal("Not checked by the LLM yet", RenderedPage.Named(page, "export-not-checked"));
        Assert.StartsWith("Approved, and the rules find nothing to stop it. Proofreading with the LLM isn't switched on yet, so read each block",
            RenderedPage.Named(page, "export-not-checked-why"));
        // Each block's own label is the word alone: one explanation, not a button per block.
        Assert.Equal("Not checked by the LLM yet", RenderedPage.Named(page, "export-not-checked-0"));
        Assert.DoesNotMatch("<button", Regex.Match(page, "<span[^>]*data-testid=\"export-not-checked-0\"[^>]*>.*?</span>", RegexOptions.Singleline).Value);
    }

    // What How it works says of the LLM is what is true on the demo: no page dismisses a finding.
    [Fact]
    public async Task How_it_works_off_says_what_is_true_and_promises_no_dismissal()
    {
        var page = await Get("/how-it-works");

        Assert.Equal(HowItWorksLlmTests.DemoLine, RenderedPage.Named(page, "how-llm"));
        Assert.Equal("The rules above are real and run as this page loads. Proofreading with the LLM is not switched on yet.",
            RenderedPage.Named(page, "how-llm-status"));
        Assert.DoesNotContain("dismiss", RenderedPage.Text(page), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("your name and a reason", RenderedPage.Text(page));
    }
}

/// <summary>The demo with the proofread switched on: How it works says so, and the LLM's places are explained.</summary>
public class HowItWorksLlmTests(ProofreadDemoApp app) : IClassFixture<ProofreadDemoApp>
{
    internal const string DemoLine =
        "Then an LLM reads it the way a careful editor would: spelling, prices that do not match, contradictions, placeholder text left in. " +
        "If it is wrong about something worth a look, you can leave it as it is. If it is wrong about something it says must be fixed, " +
        "that version waits until you edit it and save again. If the LLM cannot run, the rules' checks still stand.";

    private static readonly Finding Unclear = new(Severity.Warning, "ai-clarity", "Offer", "Could read two ways. Suggest: say per visit.", "per visit");
    private static readonly Finding Misspelt = new(Severity.Blocker, "ai-spelling", "Opening", "Misspelt. Suggest: receive", "recieve");

    private async Task<string> Get(string path)
    {
        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Switched_on_how_it_works_says_so_and_where_the_button_is_and_never_calls_the_llm()
    {
        var page = await Get("/how-it-works");

        Assert.Equal("The rules above are real and run as this page loads. Proofreading with the LLM is switched on: " +
            "on a saved campaign in Campaigns, use the \"Proofread with the LLM\" button.", LlmTermOffTests.Prose(RenderedPage.Named(page, "how-llm-status")));
        Assert.Equal("Try it: once a campaign is approved, its blocks appear on its page in Campaigns, each with a copy button. " +
            "Unless that version is proofread with the LLM first, each block is marked \"Not checked by the LLM yet\".", LlmTermOffTests.Prose(RenderedPage.Named(page, "how-export")));
        Assert.Equal(DemoLine, RenderedPage.Named(page, "how-llm"));
        Assert.DoesNotContain("dismiss", RenderedPage.Text(page), StringComparison.OrdinalIgnoreCase);
        LlmTermOffTests.Explains(page, "llm-term-how");
        // Reading the page spends nothing: the switch is read, the LLM is never asked.
        Assert.Empty(app.Proofreader.Read);
    }

    [Fact]
    public async Task The_report_s_heading_and_the_line_under_the_checks_explain_llm()
    {
        var save = await app.Stores.Campaigns(DemoApp.Client, Actor.Demo).SaveDraftAsync(Guid.NewGuid(), "Hello", DraftFixtures.Finished());

        var page = await Get($"/campaigns/{save.Id}");

        LlmTermOffTests.Explains(page, "llm-term-report");
        LlmTermOffTests.Explains(page, "llm-term-checks");
        Assert.Equal(0, RenderedPage.Count(page, "llm-term-banner"));
    }

    // In a list of LLM findings, the first tag opens the explanation and the rest are the word alone.
    [Fact]
    public async Task Only_the_first_llm_tag_in_a_list_explains_it()
    {
        var store = app.Stores.Campaigns(DemoApp.Client, Actor.Demo);
        var save = await store.SaveDraftAsync(Guid.NewGuid(), "Hello", ProofreadPageTests.Plain());
        await store.SaveProofreadAsync(save, new ProofreadRecord(app.Clock.GetUtcNow(), [Unclear, Misspelt], []));

        var page = await Get($"/campaigns/{save.Id}");

        // Must fix first, as the list orders them.
        LlmTermOffTests.Explains(page, $"finding-ai-{Misspelt.SeenKey}");
        Assert.Equal("LLM", RenderedPage.Named(page, $"finding-ai-{Unclear.SeenKey}"));
        Assert.Matches($"<span data-testid=\"{Regex.Escape($"finding-ai-{Unclear.SeenKey}")}\">LLM</span>", page);
    }
}

/// <summary>The explanation is the glossary's, whatever it says: another glossary, other words on the page.</summary>
public sealed class OtherGlossaryApp : InMemoryApp
{
    internal const string Words = "A test explanation, from the data and nowhere else.";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(PrototypeCallerSource.ClientSetting, DemoApp.Client.ToString());
        base.ConfigureWebHost(builder);
    }

    // Program's own access stands, the demo's; only the glossary is another.
    protected override void ConfigureAccess(IServiceCollection services) =>
        services.RemoveAll<Glossary>().AddSingleton(Glossary.Load(
            $$"""{ "terms": [ { "term": "LLM", "explanation": "{{Words}}" } ] }""", Glossary.Explained));
}

public class LlmExplanationIsDataTests(OtherGlossaryApp app) : IClassFixture<OtherGlossaryApp>
{
    [Fact]
    public async Task The_page_says_what_the_glossary_says()
    {
        var response = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/how-it-works");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(OtherGlossaryApp.Words, RenderedPage.Named(await response.Content.ReadAsStringAsync(), "llm-explained"));
    }
}

/// <summary>
/// "LLM", not "AI", in every word the product says, not only on the pages: the export's refusal, the
/// startup refusals and the log lines an operator reads. This scans the source the app is built from,
/// with its comments taken out, so a comment may still say AI and nothing the app says can. The
/// glossary is not scanned: its explanation of LLM may say AI (owner, 2026-10-10).
/// </summary>
public class ProductWordingTests
{
    private static readonly string[] Scanned = [".cs", ".razor", ".html", ".js"];

    private static readonly Regex[] CommentPatterns =
    [
        new(@"@\*.*?\*@", RegexOptions.Singleline),
        new(@"/\*.*?\*/", RegexOptions.Singleline),
        new("<!--.*?-->", RegexOptions.Singleline),
        // Not after a colon, so an address such as https://... is not taken for a comment.
        new(@"(?<!:)//.*$", RegexOptions.Multiline),
    ];

    private static readonly Regex Ai = new(@"\bAI\b");

    [Fact]
    public void Nothing_the_app_says_calls_the_llm_ai()
    {
        var found = SourceFiles()
            .SelectMany(file => SaysAi(File.ReadAllText(file)).Select(line => $"{Path.GetRelativePath(InfrastructureTests.Root, file)}: {line}"))
            .ToList();

        Assert.True(found.Count == 0, "Say LLM, not AI: " + string.Join("; ", found));
    }

    // Built here rather than written out in a scanned file.
    [Fact]
    public void The_scan_finds_ai_in_what_is_said_and_passes_comments()
    {
        var ai = "A" + "I";
        Assert.NotEmpty(SaysAi($"throw new X(\"Campaign has not been through the {ai} proofread.\");"));
        Assert.NotEmpty(SaysAi($"<p>Proofread by {ai}</p>"));
        Assert.NotEmpty(SaysAi($"var url = \"https://x\"; log.LogWarning(\"The {ai} proofread could not be reached.\");"));
        Assert.Empty(SaysAi($"/// <summary>The {ai} proofread.</summary>\n// the {ai}'s findings\nvar ai = 1;"));
        Assert.Empty(SaysAi($"@* What the {ai} found *@ /* the {ai} */ <!-- {ai} -->"));
        Assert.Empty(SaysAi("\"ai-spelling\", \"FAIL\", \"MAIL\""));
    }

    [Fact]
    public void The_scan_covers_the_files_that_matter()
    {
        var files = SourceFiles().Select(f => Path.GetRelativePath(InfrastructureTests.Root, f).Replace('\\', '/')).ToList();

        Assert.Contains("src/Neelam.Campaigns/EditorExport.cs", files);
        Assert.Contains("src/Neelam.Campaigns.Claude/ClaudeProofreader.cs", files);
        Assert.Contains("src/Neelam.Web/Program.cs", files);
        Assert.Contains("src/Neelam.Web/Components/Pages/CampaignEdit.razor", files);
        Assert.Contains("src/Neelam.Web/wwwroot/startup-failed.html", files);
        Assert.DoesNotContain(files, f => f.Contains("/obj/") || f.Contains("/bin/"));
    }

    /// <summary>Each line that says AI once the comments are out.</summary>
    private static IEnumerable<string> SaysAi(string source)
    {
        var said = CommentPatterns.Aggregate(source, (text, comment) => comment.Replace(text, ""));
        return said.Split('\n').Where(line => Ai.IsMatch(line)).Select(line => line.Trim());
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(InfrastructureTests.Root, "src"), "*", SearchOption.AllDirectories)
            .Where(f => Scanned.Contains(Path.GetExtension(f)))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));
}

/// <summary>What the LLM report's line says it found (owner, 2026-10-10: "LLM report, 10 Oct 2:46 PM: 5 things worth a look.").</summary>
public class ProofreadSummaryTests
{
    [Theory]
    [InlineData(0, 0, "nothing to fix in this version.")]
    [InlineData(0, 1, "1 thing worth a look.")]
    [InlineData(0, 5, "5 things worth a look.")]
    [InlineData(1, 0, "1 thing to fix.")]
    [InlineData(2, 0, "2 things to fix.")]
    [InlineData(1, 4, "1 thing to fix, 4 worth a look.")]
    [InlineData(3, 1, "3 things to fix, 1 worth a look.")]
    public void It_counts_what_the_llm_found_by_how_much_it_matters(int mustFix, int worthALook, string expected)
    {
        var findings = Enumerable.Range(0, mustFix).Select(i => new Finding(Severity.Blocker, "ai-spelling", "Offer", $"Fix {i}"))
            .Concat(Enumerable.Range(0, worthALook).Select(i => new Finding(Severity.Warning, "ai-clarity", "Offer", $"Look {i}")))
            .ToList();

        Assert.Equal(expected, DraftSession.ProofreadSummary(new ProofreadRecord(DateTimeOffset.UnixEpoch, findings, [])));
    }
}
