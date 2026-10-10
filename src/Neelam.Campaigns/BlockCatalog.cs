using System.Text.Json;

namespace Neelam.Campaigns;

/// <summary>
/// The block types the editor offers, in the order its "Add a block" list shows them, and what each
/// is for and what the checks look for in it. Data, not code (owner, 2026-10-09: "Let's change the
/// way these are created into json templates that are read in. This will allow more customization of
/// template structures."): <c>Catalog/blocks.json</c> says what each block is, and
/// <c>Catalog/rules.json</c> describes each rule once. Read and checked once, at startup: when the
/// files and the code disagree, the app does not start (<see cref="BlockCatalogException"/>).
/// </summary>
/// <remarks>
/// One catalog serves every client today: the shipped one, registered in DI. A client's own catalog,
/// over an operator standard as with the template baseline and the tier ladders, would be another
/// <see cref="BlockCatalog"/> made by <see cref="Load"/> and handed to the editor the same way; the
/// editor and the pages already take the catalog rather than reaching for a static one.
/// </remarks>
public sealed class BlockCatalog
{
    /// <summary>The folder under the app's base directory the shipped files are copied to.</summary>
    public const string Folder = "Catalog";

    public const string BlocksFile = "blocks.json";

    public const string RulesFile = "rules.json";

    private readonly Dictionary<BlockType, BlockGuide> _byType;

    private BlockCatalog(IReadOnlyList<BlockGuide> all)
    {
        All = all;
        _byType = all.ToDictionary(g => g.Type);
    }

    /// <summary>Every block type, in the order of <c>blocks.json</c>: the order "Add a block" lists them.</summary>
    public IReadOnlyList<BlockGuide> All { get; }

    public BlockGuide For(BlockType type) => _byType[type];

    /// <summary>
    /// The catalog shipped with the app, from <c>Catalog/</c> under <paramref name="baseDirectory"/>,
    /// checked against the block types in code and the rules the checks report
    /// (<see cref="CheckRules.Reported"/>).
    /// </summary>
    public static BlockCatalog Shipped(string baseDirectory)
    {
        var folder = Path.Combine(baseDirectory, Folder);
        string Read(string file)
        {
            var path = Path.Combine(folder, file);
            return File.Exists(path)
                ? File.ReadAllText(path)
                : throw new BlockCatalogException([$"{Folder}/{file} is missing from {folder}; it ships with the app."]);
        }
        return Load(Read(BlocksFile), Read(RulesFile), CheckRules.Reported);
    }

    /// <summary>
    /// A catalog from the two files' text, checked against the block types in code and
    /// <paramref name="reportable"/>, the rule ids the checks can report. Every problem found is
    /// listed in the one exception, so one start says everything that needs fixing.
    /// </summary>
    public static BlockCatalog Load(string blocksJson, string rulesJson, IReadOnlyCollection<string> reportable)
    {
        var problems = new List<string>();
        var blocks = Parse<BlocksFileShape>(blocksJson, BlocksFile, problems)?.Blocks;
        var rules = Parse<RulesFileShape>(rulesJson, RulesFile, problems)?.Rules;
        if (problems.Count > 0) throw new BlockCatalogException(problems);
        if (blocks is null) problems.Add($"{BlocksFile} has no \"blocks\" list.");
        if (rules is null) problems.Add($"{RulesFile} has no \"rules\" list.");
        if (problems.Count > 0) throw new BlockCatalogException(problems);

        var lines = Lines(rules!, reportable, problems);
        var guides = Guides(blocks!, lines, reportable, problems);
        if (problems.Count > 0) throw new BlockCatalogException(problems);
        return new BlockCatalog(guides);
    }

    // Each rule id's line: its own description, or that of the rule it is described with.
    private static Dictionary<string, string> Lines(
        IReadOnlyList<RuleShape?> rules, IReadOnlyCollection<string> reportable, List<string> problems)
    {
        var own = new Dictionary<string, string>(StringComparer.Ordinal);
        var with = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule?.Id))
            {
                problems.Add($"{RulesFile} has a rule with no id.");
                continue;
            }
            if (!seen.Add(rule.Id))
            {
                problems.Add($"{RulesFile} lists the rule '{rule.Id}' twice; describe each rule once.");
                continue;
            }
            if (!reportable.Contains(rule.Id))
                problems.Add($"{RulesFile} describes the rule '{rule.Id}', which no check reports.");
            var hasOwn = !string.IsNullOrWhiteSpace(rule.Description);
            var hasWith = !string.IsNullOrWhiteSpace(rule.DescribedWith);
            if (hasOwn == hasWith)
                problems.Add(hasOwn
                    ? $"{RulesFile}: the rule '{rule.Id}' has both a description and \"describedWith\"; give it one or the other."
                    : $"{RulesFile}: the rule '{rule.Id}' has no description; give it one, or \"describedWith\" naming the rule whose line it shares.");
            else if (hasOwn)
                own[rule.Id] = rule.Description!.Trim();
            else
                with[rule.Id] = rule.DescribedWith!.Trim();
        }

        var lines = new Dictionary<string, string>(own, StringComparer.Ordinal);
        foreach (var (id, target) in with)
        {
            if (own.TryGetValue(target, out var line)) lines[id] = line;
            else problems.Add($"{RulesFile}: the rule '{id}' is described with '{target}', which has no description of its own.");
        }
        foreach (var id in reportable.Where(id => !seen.Contains(id)).Order(StringComparer.Ordinal))
            problems.Add($"{RulesFile} has no description for the rule '{id}', which the checks report.");
        return lines;
    }

    private static List<BlockGuide> Guides(
        IReadOnlyList<BlockShape?> blocks, Dictionary<string, string> lines, IReadOnlyCollection<string> reportable, List<string> problems)
    {
        var guides = new List<BlockGuide>();
        var types = new HashSet<BlockType>();
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block?.Type))
            {
                problems.Add($"{BlocksFile} has a block with no type.");
                continue;
            }
            // By name only: Enum.TryParse would also take "3" or "header".
            if (!Enum.GetNames<BlockType>().Contains(block.Type, StringComparer.Ordinal))
            {
                problems.Add($"{BlocksFile} names the block type '{block.Type}', which the code does not have. " +
                    $"The types are: {string.Join(", ", Enum.GetNames<BlockType>())}.");
                continue;
            }
            var type = Enum.Parse<BlockType>(block.Type);
            if (!types.Add(type))
            {
                problems.Add($"{BlocksFile} lists the block type '{type}' twice.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(block.Label)) problems.Add($"{BlocksFile}: the block type '{type}' has no label.");
            if (string.IsNullOrWhiteSpace(block.Description)) problems.Add($"{BlocksFile}: the block type '{type}' has no description.");

            var named = block.Rules ?? [];
            var problemsBefore = problems.Count;
            foreach (var repeated in named.GroupBy(r => r, StringComparer.Ordinal).Where(g => g.Count() > 1))
                problems.Add($"{BlocksFile}: the block type '{type}' names the rule '{repeated.Key}' twice.");
            foreach (var unknown in named.Where(r => !reportable.Contains(r) && !string.IsNullOrWhiteSpace(r)).Distinct())
                problems.Add($"{BlocksFile}: the block type '{type}' names the rule '{unknown}', which no check reports.");
            // A reported rule rules.json does not describe is already a problem of its own.
            if (named.Any(r => reportable.Contains(r) && !lines.ContainsKey(r))) problems.Add($"{BlocksFile}: the block type '{type}' names a rule with no description.");
            if (named.Any(string.IsNullOrWhiteSpace)) problems.Add($"{BlocksFile}: the block type '{type}' names an empty rule id.");

            // This block's own wording, by rule: said once, where the first of its rules stands.
            var wordingOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var wording in block.Wording ?? [])
            {
                if (string.IsNullOrWhiteSpace(wording?.Says) || wording.Rules is not { Count: > 0 })
                {
                    problems.Add($"{BlocksFile}: the block type '{type}' has wording with no rules or nothing it says.");
                    continue;
                }
                foreach (var rule in wording.Rules)
                {
                    if (!named.Contains(rule, StringComparer.Ordinal))
                        problems.Add($"{BlocksFile}: the block type '{type}' words the rule '{rule}', which it does not name in its rules.");
                    else if (!wordingOf.TryAdd(rule, wording.Says.Trim()))
                        problems.Add($"{BlocksFile}: the block type '{type}' words the rule '{rule}' twice.");
                }
            }
            if (problems.Count > problemsBefore) continue;

            var checks = named.Select(r => wordingOf.TryGetValue(r, out var says) ? says : lines[r]).Distinct(StringComparer.Ordinal).ToList();
            guides.Add(new BlockGuide(type, block.Label?.Trim() ?? "", block.Description?.Trim() ?? "", checks, [.. named]));
        }
        foreach (var missing in Enum.GetValues<BlockType>().Where(t => !types.Contains(t)))
            problems.Add($"{BlocksFile} has no entry for the block type '{missing}'; every block type in the code needs one.");
        return guides;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
    };

    private static T? Parse<T>(string json, string file, List<string> problems) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new JsonException("The file is empty or null.");
        }
        catch (JsonException e)
        {
            problems.Add($"{file} is not valid: {e.Message}");
            return null;
        }
    }

    // The files' shapes, as read. Unknown properties are refused, so a misspelt name is not ignored.
    private sealed record BlocksFileShape(IReadOnlyList<BlockShape?>? Blocks);

    private sealed record BlockShape(string? Type, string? Label, string? Description, IReadOnlyList<string>? Rules, IReadOnlyList<WordingShape?>? Wording);

    private sealed record WordingShape(IReadOnlyList<string>? Rules, string? Says);

    private sealed record RulesFileShape(IReadOnlyList<RuleShape?>? Rules);

    private sealed record RuleShape(string? Id, string? Description, string? DescribedWith);
}

/// <summary>The block catalog's files and the code disagree, so the app does not start; the message lists every problem.</summary>
public sealed class BlockCatalogException(IReadOnlyList<string> problems)
    : InvalidOperationException(
        "The block catalog (Catalog/blocks.json and Catalog/rules.json) does not match the code:" +
        string.Concat(problems.Select(p => $"{Environment.NewLine}- {p}")))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
