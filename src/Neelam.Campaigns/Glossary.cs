using System.Text.Json;

namespace Neelam.Campaigns;

/// <summary>
/// What the words the pages explain mean, said once: data, not code (house rule 3, text people read
/// is JSON read in, one source each), in <c>Catalog/glossary.json</c> beside the block catalog. Read
/// and checked once, at startup: when the file and the code disagree, the app does not start
/// (<see cref="GlossaryException"/>). The first word is "LLM" (owner, 2026-10-10: "I prefer llm to
/// ai", and of explaining it, "Having a tooltip is great; it lets her discover any time she stumbles
/// across it").
/// </summary>
public sealed class Glossary
{
    public const string FileName = "glossary.json";

    /// <summary>The large language model the proofread uses, as the pages name it.</summary>
    public const string Llm = "LLM";

    /// <summary>Every word the pages explain, so each must be in the file.</summary>
    public static IReadOnlyList<string> Explained { get; } = [Llm];

    private readonly Dictionary<string, string> _explanations;

    private Glossary(Dictionary<string, string> explanations) => _explanations = explanations;

    /// <summary>What <paramref name="term"/> means, as the file says it.</summary>
    public string Explain(string term) => _explanations[term];

    /// <summary>The glossary shipped with the app, from <c>Catalog/</c> under <paramref name="baseDirectory"/>.</summary>
    public static Glossary Shipped(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, BlockCatalog.Folder, FileName);
        return File.Exists(path)
            ? Load(File.ReadAllText(path), Explained)
            : throw new GlossaryException([$"{BlockCatalog.Folder}/{FileName} is missing from {Path.GetDirectoryName(path)}; it ships with the app."]);
    }

    /// <summary>
    /// A glossary from the file's text, checked to explain every word in <paramref name="explained"/>.
    /// Every problem found is listed in the one exception, so one start says everything to fix.
    /// </summary>
    public static Glossary Load(string json, IReadOnlyCollection<string> explained)
    {
        var problems = new List<string>();
        FileShape? file = null;
        try
        {
            file = JsonSerializer.Deserialize<FileShape>(json, Options) ?? throw new JsonException("The file is empty or null.");
        }
        catch (JsonException e)
        {
            problems.Add($"{FileName} is not valid: {e.Message}");
        }
        if (file is not null && file.Terms is null) problems.Add($"{FileName} has no \"terms\" list.");
        if (problems.Count > 0) throw new GlossaryException(problems);

        var explanations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in file!.Terms!)
        {
            if (string.IsNullOrWhiteSpace(entry?.Term))
            {
                problems.Add($"{FileName} has an entry with no term.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.Explanation))
                problems.Add($"{FileName}: the term '{entry.Term}' has no explanation.");
            else if (!explanations.TryAdd(entry.Term, entry.Explanation.Trim()))
                problems.Add($"{FileName} explains the term '{entry.Term}' twice; explain each once.");
        }
        foreach (var term in explained.Where(t => !explanations.ContainsKey(t)))
            problems.Add($"{FileName} does not explain '{term}', which the pages explain.");
        if (problems.Count > 0) throw new GlossaryException(problems);
        return new Glossary(explanations);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
    };

    // The file's shape, as read. Unknown properties are refused, so a misspelt name is not ignored.
    private sealed record FileShape(IReadOnlyList<EntryShape?>? Terms);

    private sealed record EntryShape(string? Term, string? Explanation);
}

/// <summary>The glossary's file and the code disagree, so the app does not start; the message lists every problem.</summary>
public sealed class GlossaryException(IReadOnlyList<string> problems)
    : InvalidOperationException(
        $"The glossary ({BlockCatalog.Folder}/{Glossary.FileName}) does not match the code:" +
        string.Concat(problems.Select(p => $"{Environment.NewLine}- {p}")))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
