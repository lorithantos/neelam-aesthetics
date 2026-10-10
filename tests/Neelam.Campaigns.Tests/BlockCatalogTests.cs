using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Neelam.Campaigns.Tests;

/// <summary>The block catalog the tests build editors with: the files the app ships, read as the app reads them.</summary>
internal static class Catalogs
{
    public static BlockCatalog Shipped { get; } = BlockCatalog.Shipped(AppContext.BaseDirectory);
}

/// <summary>
/// Block definitions and the rules' descriptions are data read at startup (owner, 2026-10-09: "Let's
/// change the way these are created into json templates that are read in."): the shipped files load,
/// say what the code said before except the Fine print block's contradiction, and every way the files
/// and the code can disagree stops the app with a message saying where.
/// </summary>
public class BlockCatalogTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private const string MedicalLine = "A medical term needs a disclaimer in a fine-print block.";
    private const string FinePrintLine = "Any text here counts as the disclaimer a medical term needs.";

    // Each block's guide as BlockGuide.All had it in code before the catalog, captured from that code.
    private static readonly IReadOnlyList<Before> Snapshot = JsonSerializer.Deserialize<List<Before>>(
        File.ReadAllText(Path.Combine(InfrastructureTests.Root, "tests", "Neelam.Campaigns.Tests", "block-guides-before.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed record Before(string Type, string Label, string Description, List<string> Checks, List<string> Rules);

    private static string CatalogFile(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, BlockCatalog.Folder, file));

    // ---- The shipped files

    [Fact]
    public void The_shipped_files_load_and_name_every_block_type_once()
    {
        var catalog = BlockCatalog.Load(CatalogFile(BlockCatalog.BlocksFile), CatalogFile(BlockCatalog.RulesFile), CheckRules.Reported);

        Assert.Equal(Enum.GetValues<BlockType>().Order(), catalog.All.Select(g => g.Type).Order());
        Assert.All(Enum.GetValues<BlockType>(), t => Assert.Equal(t, catalog.For(t).Type));
    }

    // Shipped with the web app, not only with the tests: the app reads them from its own output.
    [Fact]
    public void The_web_app_is_given_the_shipped_catalog()
    {
        var catalog = app.Services.GetRequiredService<BlockCatalog>();

        Assert.Equal(Catalogs.Shipped.All.Select(g => (g.Type, g.Label, string.Join("|", g.Checks))),
            catalog.All.Select(g => (g.Type, g.Label, string.Join("|", g.Checks))));
    }

    [Fact]
    public void The_add_block_list_keeps_its_order()
    {
        Assert.Equal(Snapshot.Select(b => b.Type), Catalogs.Shipped.All.Select(g => g.Type.ToString()));
    }

    // Every block but Fine print reads exactly as it did when the guide was code.
    [Fact]
    public void Every_other_block_s_guide_is_unchanged()
    {
        Assert.All(Snapshot.Where(b => b.Type != nameof(BlockType.FinePrint)), before =>
        {
            var now = Catalogs.Shipped.For(Enum.Parse<BlockType>(before.Type));
            Assert.Equal(before.Label, now.Label);
            Assert.Equal(before.Description, now.Description);
            Assert.Equal(before.Checks, now.Checks);
            Assert.Equal(before.Rules, now.Rules);
        });
    }

    // The contradiction she found on her phone: a fine-print block told her both that its text is the
    // disclaimer and that a medical term needs one in a fine-print block.
    [Fact]
    public void The_fine_print_guide_says_its_text_is_the_disclaimer_and_no_longer_asks_for_one()
    {
        var before = Snapshot.Single(b => b.Type == nameof(BlockType.FinePrint));
        var now = Catalogs.Shipped.For(BlockType.FinePrint);

        Assert.Contains(MedicalLine, before.Checks);
        Assert.DoesNotContain(MedicalLine, now.Checks);
        Assert.Equal(FinePrintLine, now.Checks[0]);
        Assert.Equal(before.Checks.Where(c => c != MedicalLine), now.Checks);
        Assert.Equal(before.Label, now.Label);
        Assert.Equal(before.Description, now.Description);
        Assert.Equal(before.Rules.Order(), now.Rules.Order());
    }

    // ---- The rules the checks report

    // The list the catalog is checked against at startup is the ids CampaignReview writes, both ways.
    [Fact]
    public void Check_rules_are_exactly_the_rule_ids_the_checks_report()
    {
        var review = File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Campaigns", "CampaignReview.cs"));
        var reported = Regex.Matches(review, "\"([a-z]+(?:-[a-z]+)+)\"").Select(m => m.Groups[1].Value).ToHashSet();

        Assert.Equal(reported.Order(), CheckRules.Reported.Order());
        Assert.Equal(CheckRules.Reported.Count, CheckRules.Reported.Distinct().Count());
    }

    // Each rule is described once: no two rules carry the same words of their own.
    [Fact]
    public void Each_rule_is_described_once()
    {
        var rules = JsonNode.Parse(CatalogFile(BlockCatalog.RulesFile))!["rules"]!.AsArray();
        var descriptions = rules.Select(r => r!["description"]?.GetValue<string>()).OfType<string>().ToList();

        Assert.Equal(descriptions.Count, descriptions.Distinct().Count());
    }

    // ---- Refusals: the app does not start, and says why

    public static TheoryData<string, string> Refusals => new()
    {
        { "no-entry", "blocks.json has no entry for the block type 'Spacer'; every block type in the code needs one." },
        { "unknown-type", "blocks.json names the block type 'Footer', which the code does not have." },
        { "block-unknown-rule", "blocks.json: the block type 'Heading' names the rule 'made-up', which no check reports." },
        { "rules-unknown-rule", "rules.json describes the rule 'made-up', which no check reports." },
        { "undescribed", "rules.json has no description for the rule 'second-rule', which the checks report." },
        { "duplicate-type", "blocks.json lists the block type 'Header' twice." },
        { "duplicate-rule", "rules.json lists the rule 'first-rule' twice; describe each rule once." },
        { "duplicate-in-block", "blocks.json: the block type 'Heading' names the rule 'first-rule' twice." },
        { "both", "rules.json: the rule 'second-rule' has both a description and \"describedWith\"; give it one or the other." },
        { "neither", "rules.json: the rule 'second-rule' has no description; give it one, or \"describedWith\" naming the rule whose line it shares." },
        { "described-with-nothing", "rules.json: the rule 'second-rule' is described with 'made-up', which has no description of its own." },
        { "wording-unnamed", "blocks.json: the block type 'Header' words the rule 'second-rule', which it does not name in its rules." },
        { "no-label", "blocks.json: the block type 'Header' has no label." },
        { "misspelt", "blocks.json is not valid:" },
        { "not-json", "rules.json is not valid:" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_catalog_that_disagrees_with_the_code_is_refused_saying_where(string fault, string message)
    {
        var (blocks, rules) = Minimal();
        var heading = blocks.Single(b => b!["type"]!.GetValue<string>() == "Heading")!;
        var header = blocks.Single(b => b!["type"]!.GetValue<string>() == "Header")!;
        switch (fault)
        {
            case "no-entry": blocks.Remove(blocks.Single(b => b!["type"]!.GetValue<string>() == "Spacer")); break;
            case "unknown-type": blocks.Add(Block("Footer")); break;
            case "block-unknown-rule": heading["rules"]!.AsArray().Add("made-up"); break;
            case "rules-unknown-rule": rules.Add(new JsonObject { ["id"] = "made-up", ["description"] = "Made up." }); break;
            case "undescribed": rules.RemoveAt(1); break;
            case "duplicate-type": blocks.Add(Block("Header")); break;
            case "duplicate-rule": rules.Add(new JsonObject { ["id"] = "first-rule", ["description"] = "Again." }); break;
            case "duplicate-in-block": heading["rules"]!.AsArray().Add("first-rule"); break;
            case "both": rules[1]!["description"] = "Its own."; break;
            case "neither": rules[1]!.AsObject().Remove("describedWith"); break;
            case "described-with-nothing": rules[1]!["describedWith"] = "made-up"; break;
            case "wording-unnamed": header["wording"] = new JsonArray(new JsonObject { ["rules"] = new JsonArray("second-rule"), ["says"] = "Said." }); break;
            case "no-label": header["label"] = " "; break;
            case "misspelt": heading["rule"] = new JsonArray(); break;
        }
        var rulesJson = fault == "not-json" ? "{ \"rules\": [" : Rules(rules);

        var refused = Assert.Throws<BlockCatalogException>(() => BlockCatalog.Load(Blocks(blocks), rulesJson, Reportable));

        Assert.Contains(refused.Problems, p => p.StartsWith(message, StringComparison.Ordinal));
        Assert.StartsWith("The block catalog (Catalog/blocks.json and Catalog/rules.json) does not match the code:", refused.Message);
        Assert.Contains(message, refused.Message);
    }

    // The minimal catalog the refusals start from is itself accepted, and its guide says what it should.
    [Fact]
    public void The_minimal_catalog_loads_with_shared_and_block_wording()
    {
        var (blocks, rules) = Minimal();
        blocks.Single(b => b!["type"]!.GetValue<string>() == "Greeting")!["wording"] =
            new JsonArray(new JsonObject { ["rules"] = new JsonArray("first-rule", "second-rule"), ["says"] = "Greeting's own words." });

        var catalog = BlockCatalog.Load(Blocks(blocks), Rules(rules), Reportable);

        // Described with the first rule: one line for both.
        Assert.Equal(["The first rule."], catalog.For(BlockType.Heading).Checks);
        Assert.Equal(["first-rule", "second-rule"], catalog.For(BlockType.Heading).Rules);
        Assert.Equal(["Greeting's own words."], catalog.For(BlockType.Greeting).Checks);
        Assert.Empty(catalog.For(BlockType.Spacer).Checks);
    }

    // A missing file is named, for a deployment that left it behind.
    [Fact]
    public void A_missing_file_is_refused_by_name()
    {
        var empty = Directory.CreateTempSubdirectory("neelam-catalog-").FullName;
        try
        {
            var refused = Assert.Throws<BlockCatalogException>(() => BlockCatalog.Shipped(empty));
            Assert.StartsWith("Catalog/blocks.json is missing from", Assert.Single(refused.Problems));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    private static readonly string[] Reportable = ["first-rule", "second-rule"];

    // Every block type with a label and a description; Heading and Greeting name both rules, and the
    // second rule is described with the first.
    private static (JsonArray Blocks, JsonArray Rules) Minimal()
    {
        var blocks = new JsonArray(Enum.GetNames<BlockType>().Select(t => (JsonNode)Block(t)).ToArray());
        foreach (var type in new[] { "Heading", "Greeting" })
            blocks.Single(b => b!["type"]!.GetValue<string>() == type)!["rules"] = new JsonArray("first-rule", "second-rule");
        var rules = new JsonArray(
            new JsonObject { ["id"] = "first-rule", ["description"] = "The first rule." },
            new JsonObject { ["id"] = "second-rule", ["describedWith"] = "first-rule" });
        return (blocks, rules);
    }

    private static JsonObject Block(string type) =>
        new() { ["type"] = type, ["label"] = $"{type} label", ["description"] = $"What a {type} is.", ["rules"] = new JsonArray() };

    private static string Blocks(JsonArray blocks) => new JsonObject { ["blocks"] = blocks.DeepClone() }.ToJsonString();

    private static string Rules(JsonArray rules) => new JsonObject { ["rules"] = rules.DeepClone() }.ToJsonString();

    // ---- The published shape, checked here only: the app reads the files with System.Text.Json

    [Theory]
    [InlineData("blocks.schema.json", BlockCatalog.BlocksFile)]
    [InlineData("rules.schema.json", BlockCatalog.RulesFile)]
    public void The_shipped_files_match_their_schemas(string schema, string file)
    {
        Assert.Empty(SchemaProblems(schema, CatalogFile(file)));
    }

    [Theory]
    [InlineData("blocks.schema.json", "{ \"blocks\": [ { \"type\": \"Header\", \"label\": \"Header\", \"description\": \"Top.\", \"rules\": [], \"colour\": \"red\" } ] }")]
    [InlineData("blocks.schema.json", "{ \"blocks\": [ { \"type\": \"Header\", \"description\": \"Top.\", \"rules\": [] } ] }")]
    [InlineData("rules.schema.json", "{ \"rules\": [ { \"id\": \"Not Kebab\", \"description\": \"x\" } ] }")]
    [InlineData("rules.schema.json", "{ \"rules\": [ { \"id\": \"a-rule\", \"description\": \"x\", \"describedWith\": \"b-rule\" } ] }")]
    public void The_schemas_refuse_a_wrong_shape(string schema, string json)
    {
        Assert.NotEmpty(SchemaProblems(schema, json));
    }

    private static IReadOnlyList<string> SchemaProblems(string schema, string json)
    {
        var path = Path.Combine(InfrastructureTests.Root, "docs", "block-catalog", schema);
        var results = JsonSchema.FromText(File.ReadAllText(path)).Evaluate(JsonNode.Parse(json), new EvaluationOptions { OutputFormat = OutputFormat.List });
        return results.IsValid
            ? []
            : (results.Details ?? []).Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")).DefaultIfEmpty("invalid").ToList();
    }
}
