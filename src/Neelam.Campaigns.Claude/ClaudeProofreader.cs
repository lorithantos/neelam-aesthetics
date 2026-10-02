using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Messages;

namespace Neelam.Campaigns.Claude;

/// <summary>
/// Proofreads a campaign with Claude. It reads the email exactly as the customer will (the
/// rendered preview) plus the tier table, and reports mistakes as findings with a verbatim
/// excerpt. It does not restyle: tone and emoji are the clinic's choice.
/// </summary>
public sealed class ClaudeProofreader(AnthropicClient client, string model = "claude-opus-5-5") : IProofreader
{
    public async Task<IReadOnlyList<Finding>> ProofreadAsync(
        Campaign campaign, CancellationToken cancellationToken = default)
    {
        var preview = EditorExport.Preview(campaign);

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = SystemPrompt,
            OutputConfig = new OutputConfig
            {
                Effort = Effort.High,
                Format = new JsonOutputFormat { Schema = Schema },
            },
            Messages = [new() { Role = Role.User, Content = UserPrompt(campaign, preview) }],
        }, cancellationToken: cancellationToken);

        if (response.StopReason == "refusal")
            throw new InvalidOperationException("Claude declined to proofread this email");
        if (response.StopReason == "max_tokens")
            throw new InvalidOperationException("the proofread was cut off before it finished");

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return ToFindings(json, preview);
    }

    /// <summary>
    /// Turns Claude's JSON into findings. An excerpt that does not appear in the email is a sign
    /// the model misread it, so that finding is downgraded to a warning rather than trusted.
    /// </summary>
    public static IReadOnlyList<Finding> ToFindings(string json, string preview)
    {
        var result = JsonSerializer.Deserialize<ProofreadResult>(json, JsonOptions)
                     ?? throw new InvalidOperationException("the proofread returned no result");

        return result.Findings.Select(f =>
        {
            var severity = f.Severity == "error" ? Severity.Blocker : Severity.Warning;
            var message = string.IsNullOrWhiteSpace(f.Suggestion) ? f.Problem : $"{f.Problem} Suggest: {f.Suggestion}";
            if (!Contains(preview, f.Excerpt))
            {
                severity = Severity.Warning;
                message += " (Excerpt not found in the email; check by hand.)";
            }
            return new Finding(severity, $"ai-{f.Category}", f.Location, message, f.Excerpt);
        }).ToList();
    }

    private static bool Contains(string preview, string excerpt) =>
        !string.IsNullOrWhiteSpace(excerpt)
        && Normalise(preview).Contains(Normalise(excerpt), StringComparison.OrdinalIgnoreCase);

    // The preview upper-cases headings and adds bullets; compare on words only.
    private static string Normalise(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace('’', '\'');

    private static string UserPrompt(Campaign c, string preview)
    {
        var tiers = c.Offer is null
            ? "(no tiered offer)"
            : string.Join("\n", c.Offer.Tiers.Select((t, i) =>
                $"Tier {i + 1}: name \"{t.Name}\", ${t.MonthlyPrice}/month; benefits: " +
                string.Join("; ", t.Benefits.Select(b => b.Describe()))));

        return $"""
            Subject line: {c.Subject}
            Preheader: {c.Preheader ?? "(none)"}

            Tier table (the facts the email must agree with):
            {tiers}

            The email exactly as the customer will read it:
            <email>
            {preview}
            </email>
            """;
    }

    private const string SystemPrompt = """
        You proofread marketing emails for a small, family-run medical aesthetics clinic before they
        go to customers. Your job is to catch mistakes that would embarrass the business or mislead
        a customer, the kind a careful human editor would catch on a final read. You are not
        restyling the email: its warm voice and emoji are deliberate, so leave tone, emoji, length
        and word choice alone unless something is actually wrong.

        Report as "error":
        - spelling mistakes, wrong or missing words, grammar that changes or obscures meaning
        - two offers, tiers or options that are identical, or that share a name, or whose names
          do not distinguish them
        - statements that contradict each other or the tier table, such as something described
          as both free and discounted, or a price, percentage or amount that differs between mentions
        - wrong or inconsistent names, dates, addresses or phone numbers
        - placeholder or template text left in, such as [Name], TBD, XXX or lorem ipsum

        Report as "warning":
        - the same sentence or idea repeated
        - wording a customer could reasonably read two ways
        - inconsistent capitalisation or punctuation
        - anything else a careful editor would query before sending

        For each finding, "excerpt" must be copied verbatim from the email so a person can find it.
        Keep "problem" to one sentence, and give a concrete corrected wording in "suggestion".
        If nothing is wrong, return an empty findings list; do not invent problems.
        """;

    private static readonly Dictionary<string, JsonElement> Schema = SchemaFromJson("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["findings"],
          "properties": {
            "findings": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["severity", "category", "location", "excerpt", "problem", "suggestion"],
                "properties": {
                  "severity": { "type": "string", "enum": ["error", "warning"] },
                  "category": {
                    "type": "string",
                    "enum": ["spelling", "grammar", "duplicate-offer", "contradiction", "repetition",
                             "factual", "placeholder", "clarity", "consistency", "other"]
                  },
                  "location": { "type": "string" },
                  "excerpt": { "type": "string" },
                  "problem": { "type": "string" },
                  "suggestion": { "type": "string" }
                }
              }
            }
          }
        }
        """);

    private static Dictionary<string, JsonElement> SchemaFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record ProofreadResult(
        [property: JsonPropertyName("findings")] IReadOnlyList<RawFinding> Findings);

    private sealed record RawFinding(
        string Severity, string Category, string Location, string Excerpt, string Problem, string Suggestion);
}
