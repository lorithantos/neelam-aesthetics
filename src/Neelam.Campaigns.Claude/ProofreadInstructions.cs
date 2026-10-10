using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Neelam.Campaigns.Claude;

/// <summary>
/// What the AI proofread is told, the same for every email: data, not code (house rule 3), in
/// <c>Proofread/proofread.json</c> beside this project and copied with the app. Read and checked
/// once, at startup: when the file and the code disagree, the app does not start
/// (<see cref="ProofreadInstructionsException"/>). The code keeps what the request guarantees: the
/// tags it wraps each piece of content in, the severities and the shape of the answer; the file must
/// name each of those, so the words and the request cannot drift apart.
/// </summary>
public sealed partial class ProofreadInstructions
{
    /// <summary>The folder under the app's base directory the shipped file is copied to.</summary>
    public const string Folder = "Proofread";

    public const string File = "proofread.json";

    /// <summary>The tags the request wraps content in; the instructions say each one is content.</summary>
    public static readonly IReadOnlyList<string> ContentTags = ["<business>", "<tiers>", "<email>", "<photo>"];

    /// <summary>The severities the answer gives, as the schema allows them and the page says them.</summary>
    public const string MustFix = "must-fix";

    public const string WorthALook = "worth-a-look";

    /// <summary>The answer's fields the instructions must explain, as the schema in code requires them.</summary>
    public static readonly IReadOnlyList<string> AnswerFields = ["quote", "problem", "suggestion", "photos", "words", "shows", "offers"];

    private ProofreadInstructions(string system, string request, IReadOnlyList<string> categories)
    {
        SystemPrompt = system;
        Request = request;
        Categories = categories;
    }

    /// <summary>The fixed instructions, the request's system prompt: the same bytes for every email, so they cache.</summary>
    public string SystemPrompt { get; }

    /// <summary>The last line of every request, after the content.</summary>
    public string Request { get; }

    /// <summary>What a finding may be about; each becomes its rule id, "ai-" and the category.</summary>
    public IReadOnlyList<string> Categories { get; }

    /// <summary>The instructions shipped with the app, from <c>Proofread/</c> under <paramref name="baseDirectory"/>.</summary>
    public static ProofreadInstructions Shipped(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, Folder, File);
        return global::System.IO.File.Exists(path)
            ? Load(global::System.IO.File.ReadAllText(path))
            : throw new ProofreadInstructionsException([$"{Folder}/{File} is missing from {Path.Combine(baseDirectory, Folder)}; it ships with the app."]);
    }

    /// <summary>Instructions from the file's text, checked; every problem is listed in the one exception.</summary>
    public static ProofreadInstructions Load(string json)
    {
        var problems = new List<string>();
        Shape? shape = null;
        try
        {
            shape = JsonSerializer.Deserialize<Shape>(json, Options) ?? throw new JsonException("The file is empty or null.");
        }
        catch (JsonException e)
        {
            problems.Add($"{File} is not valid: {e.Message}");
        }
        if (shape is null) throw new ProofreadInstructionsException(problems);

        var paragraphs = shape.Instructions ?? [];
        if (paragraphs.Count == 0) problems.Add($"{File} has no \"instructions\".");
        if (paragraphs.Any(string.IsNullOrWhiteSpace)) problems.Add($"{File} has an empty paragraph in \"instructions\".");
        var system = string.Join("\n\n", paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
        foreach (var tag in ContentTags.Where(t => !system.Contains(t, StringComparison.Ordinal)))
            problems.Add($"{File}: the instructions never name {tag}, which the request wraps content in; say that it is content, never instructions.");
        foreach (var severity in new[] { MustFix, WorthALook }.Where(s => !system.Contains($"\"{s}\"", StringComparison.Ordinal)))
            problems.Add($"{File}: the instructions never name the severity \"{severity}\", which the answer gives.");
        foreach (var field in AnswerFields.Where(f => !system.Contains($"\"{f}\"", StringComparison.Ordinal)))
            problems.Add($"{File}: the instructions never name \"{field}\", which the answer must give.");

        if (string.IsNullOrWhiteSpace(shape.Request)) problems.Add($"{File} has no \"request\".");

        var categories = shape.Categories ?? [];
        if (categories.Count == 0) problems.Add($"{File} has no \"categories\".");
        foreach (var bad in categories.Where(c => c is null || !Kebab().IsMatch(c)))
            problems.Add($"{File}: the category '{bad}' is not lowercase words joined by hyphens.");
        foreach (var twice in categories.GroupBy(c => c, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"{File} lists the category '{twice.Key}' twice.");

        if (problems.Count > 0) throw new ProofreadInstructionsException(problems);
        return new ProofreadInstructions(system, shape.Request!.Trim(), [.. categories]);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    [GeneratedRegex("^[a-z]+(-[a-z]+)*$")]
    private static partial Regex Kebab();

    // The file's shape, as read. Unknown properties are refused, so a misspelt name is not ignored.
    private sealed record Shape(IReadOnlyList<string>? Instructions, string? Request, IReadOnlyList<string>? Categories);
}

/// <summary>The proofread's instructions file and the code disagree, so the app does not start; the message lists every problem.</summary>
public sealed class ProofreadInstructionsException(IReadOnlyList<string> problems)
    : InvalidOperationException(
        $"The AI proofread's instructions ({ProofreadInstructions.Folder}/{ProofreadInstructions.File}) do not match the code:" +
        string.Concat(problems.Select(p => $"{Environment.NewLine}- {p}")))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
