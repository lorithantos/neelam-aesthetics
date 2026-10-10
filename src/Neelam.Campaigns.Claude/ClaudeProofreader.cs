using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;

namespace Neelam.Campaigns.Claude;

/// <summary>How each request is made: the model, how hard it thinks, and how long its answer may run.</summary>
/// <param name="Model">The model id, <c>claude-opus-5-5</c> (owner, 2026-10-09: an Opus-class model).</param>
/// <param name="Effort">
/// How much the model thinks, and so the cost and time of each proofread. Opus 5.5 always thinks
/// (adaptive), and effort is the control; its default is medium, set here explicitly.
/// </param>
/// <param name="MaxTokens">The most the answer may run to, thinking included.</param>
/// <param name="ServerSideFallback">
/// Ask the API to run a declined request again on the model it recommends for that kind of refusal
/// (<c>fallbacks: "default"</c>). The Anthropic API offers it; Microsoft Foundry does not.
/// </param>
public sealed record ProofreadRequestSettings(string Model, Effort Effort, int MaxTokens, bool ServerSideFallback);

/// <summary>
/// Proofreads a campaign with Claude. It reads the email block by block as she approves it (the same
/// Square blocks the export gives, by the same ids), the offer's facts, the business's description as
/// background, and her photos themselves; and it reports mistakes as findings with the quoted text.
/// It does not restyle: tone and emoji are the business's choice.
/// </summary>
/// <remarks>
/// The instructions are data (<see cref="ProofreadInstructions"/>), sent first as the system prompt
/// and cached, so every proofread after the first reads them at the cache price. Everything after
/// them is content, wrapped in tags the instructions name, never instructions. The answer comes back
/// as JSON to a fixed schema (structured outputs), which is parsed into the app's findings. Any way the
/// proofread can fail -- refused, cut off, unreadable, unreachable -- comes back as a
/// <see cref="ProofreadUnavailableException"/> saying so plainly. Only counts are logged: tokens,
/// photos, the stop reason; never the email, the business or what was found.
/// <para>
/// Photos (owner, 2026-10-09/10): the app fetches each one itself from Square, through
/// <see cref="SquarePhotoFetcher"/>'s checks, and sends its bytes; no address goes to anyone else to
/// fetch. A photo whose bytes it has read before for this client (by SHA-256, in
/// <see cref="BusinessContext.PhotoReadings"/>) is given as that reading instead of sent again; a new
/// or changed one is sent, and what the model reads in it is kept for next time.
/// </para>
/// </remarks>
public sealed class ClaudeProofreader(
    IAnthropicClient client,
    SquarePhotoFetcher photoFetcher,
    ProofreadInstructions instructions,
    ProofreadRequestSettings settings,
    ILogger<ClaudeProofreader> log) : IProofreader
{
    /// <summary>The model the owner chose: Opus 5.5, exactly this id.</summary>
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>The beta that lets a declined request run again server-side, in its "default" form.</summary>
    public const string ServerSideFallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>At most this many new photos are sent per proofread: each costs about a cent or two.</summary>
    public const int MaxPhotos = 10;

    /// <summary>The largest image the API takes, smaller than the most the fetcher reads.</summary>
    public const int MaxImageBytes = 5 * 1024 * 1024;

    public const string ReadNow = "Read now.";

    public const string ReadBefore = "Read before; its earlier reading was used.";

    public async Task<ProofreadResult> ProofreadAsync(
        Campaign campaign, BusinessContext? business, CancellationToken cancellationToken = default)
    {
        var parts = EditorExport.PreviewParts(campaign);
        var photos = await PlanAsync(parts, business, photoFetcher, log, cancellationToken);
        var request = Request(campaign, parts, photos, business, instructions, settings);

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The kind of failure only: an error's text is not ours to log.
            log.LogWarning("The AI proofread could not be reached: {Error}.", ex.GetType().Name);
            throw new ProofreadUnavailableException("The AI proofread could not be reached just now. Try again in a while.", ex);
        }

        LogUsage(response, photos.Fresh.Count, photos.Remembered.Count);
        var answer = Read(response, campaign, parts, instructions);
        await KeepReadingsAsync(answer.Readings, photos.Fresh, business?.PhotoReadings, cancellationToken);
        return new ProofreadResult(answer.Findings, photos.Checks);
    }

    // What the model read in each new photo, kept for next time. A failure only means it is read again.
    private async Task KeepReadingsAsync(
        IReadOnlyDictionary<string, PhotoReading> readings, IReadOnlyList<PlannedPhoto> fresh, IPhotoReadings? store, CancellationToken ct)
    {
        if (store is null) return;
        foreach (var photo in fresh)
        {
            if (!readings.TryGetValue(photo.Id, out var reading)) continue;
            try
            {
                await store.KeepAsync(photo.Sha256, reading, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("A photo reading could not be kept: {Error}.", ex.GetType().Name);
            }
        }
    }

    /// <summary>The findings in an answer, and what it read in each new photo, by the photo's id.</summary>
    internal sealed record Answered(IReadOnlyList<Finding> Findings, IReadOnlyDictionary<string, PhotoReading> Readings);

    /// <summary>
    /// What came back, as findings, or why there are none: a refusal, an answer cut off, or one that
    /// cannot be read each say so, so a proofread that did not happen is never taken for a clean one.
    /// </summary>
    internal static Answered Read(
        BetaMessage response, Campaign campaign, IReadOnlyList<PreviewPart> parts, ProofreadInstructions instructions)
    {
        var stop = response.StopReason?.Raw();
        switch (stop)
        {
            case "refusal":
                var category = response.StopDetails?.Category?.Raw();
                throw new ProofreadUnavailableException(
                    "The AI proofread declined to read this version" +
                    (string.IsNullOrEmpty(category) ? "" : $" (its safety check named \"{category}\")") +
                    ". That says nothing about the email itself; read it through yourself before it goes out.");
            case "max_tokens":
                throw new ProofreadUnavailableException("The AI proofread was cut off before it finished, so nothing it found is shown.");
            case "end_turn":
                break;
            default:
                throw new ProofreadUnavailableException($"The AI proofread stopped before it finished ({stop ?? "no reason given"}).");
        }

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        return ToFindings(json, campaign, parts, instructions);
    }

    /// <summary>
    /// Turns the JSON answer into findings, each placed in the block of the campaign it is about, so the
    /// page can take her to it. A quote that is not in the email is a sign the model misread it, so that
    /// finding becomes "Worth a look" rather than trusted; one quoted from a photo says to check it there.
    /// </summary>
    /// <exception cref="ProofreadUnavailableException">The answer is not the JSON asked for.</exception>
    internal static Answered ToFindings(
        string json, Campaign campaign, IReadOnlyList<PreviewPart> parts, ProofreadInstructions instructions)
    {
        const string unreadable = "The AI proofread's answer could not be read, so nothing it found is shown.";
        Answer? answer;
        try
        {
            answer = JsonSerializer.Deserialize<Answer>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ProofreadUnavailableException(unreadable, ex);
        }
        if (answer?.Findings is not { } findings || findings.Any(f => f is null))
            throw new ProofreadUnavailableException(unreadable);

        var text = Words(string.Join("\n", parts.Select(p => p.Block.Text).Append(campaign.Subject).Append(campaign.Preheader ?? "")));
        var found = findings.Select(f =>
        {
            var severity = f!.Severity == ProofreadInstructions.MustFix ? Severity.Blocker : Severity.Warning;
            var category = instructions.Categories.Contains(f.Category ?? "", StringComparer.Ordinal) ? f.Category : "other";
            var quote = f.Quote ?? "";
            var message = string.IsNullOrWhiteSpace(f.Suggestion) ? f.Problem ?? "" : $"{f.Problem} Suggest: {f.Suggestion}";
            var part = parts.FirstOrDefault(p => p.Id == f.Block);
            if (string.IsNullOrWhiteSpace(quote) || !text.Contains(Words(quote), StringComparison.OrdinalIgnoreCase))
            {
                severity = Severity.Warning;
                message += part?.Block.Image is not null
                    ? " (Quoted from the photo: check it there.)"
                    : " (The quote is not in the email; check by hand.)";
            }
            return new Finding(severity, $"ai-{category}", Locate(f.Block, quote, campaign, part), message, quote);
        }).ToList();

        var readings = new Dictionary<string, PhotoReading>(StringComparer.Ordinal);
        foreach (var photo in answer.Photos ?? [])
        {
            if (photo?.Photo is { Length: > 0 } id)
                readings.TryAdd(id, new PhotoReading(photo.Words ?? "", photo.Shows ?? "", photo.Offers ?? ""));
        }
        return new Answered(found, readings);
    }

    // Where a finding is, as the rule findings say it: the subject line, or the label of the campaign
    // block the quote is in (the first of a run-on text block's, if none holds it).
    private static string Locate(string? block, string quote, Campaign campaign, PreviewPart? part)
    {
        if (block == "subject") return FindingPlace.Subject;
        if (block == "preheader") return "Preheader";
        if (part is null) return "Whole email";
        foreach (var label in part.Labels)
        {
            var own = campaign.Blocks.Where(b => b.Label == label).ToList();
            var ownText = Words(string.Join("\n", EditorExport.PreviewParts(campaign with { Blocks = own }).Select(p => p.Block.Text)));
            if (quote.Length > 0 && ownText.Contains(Words(quote), StringComparison.OrdinalIgnoreCase)) return label;
        }
        return part.Labels[0];
    }

    // Compared on words only: line breaks, runs of spaces and curly apostrophes aside.
    private static string Words(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace('’', '\'');

    /// <summary>One photo the proofread will see: sent as an image (fresh) or given as its earlier reading.</summary>
    /// <param name="Id">"p1" first, for a photo sent as an image; the answer's readings name it so.</param>
    /// <param name="Blocks">The email's blocks that show it, "b1, b5".</param>
    internal sealed record PlannedPhoto(
        string Id, string Blocks, string Name, string Sha256, byte[]? Bytes, string? MediaType, PhotoReading? Reading);

    /// <summary>The photos to send, those given by their earlier reading, and what to tell her of each.</summary>
    internal sealed record PhotoPlan(IReadOnlyList<PlannedPhoto> Fresh, IReadOnlyList<PlannedPhoto> Remembered, IReadOnlyList<PhotoCheck> Checks);

    /// <summary>
    /// How each photo the email shows is dealt with, once however many blocks show it: fetched from
    /// her library's Square address through the fetcher's checks, hashed, and then given as its earlier
    /// reading when those bytes have one, or sent as an image (at most <see cref="MaxPhotos"/>). A photo
    /// that cannot be fetched is not checked, and its note says why, so the result never implies its
    /// words were read.
    /// </summary>
    internal static async Task<PhotoPlan> PlanAsync(
        IReadOnlyList<PreviewPart> parts, BusinessContext? business, SquarePhotoFetcher fetcher, ILogger log, CancellationToken ct)
    {
        var fresh = new List<PlannedPhoto>();
        var remembered = new List<PlannedPhoto>();
        var checks = new List<PhotoCheck>();
        var addresses = business?.PhotoAddresses is { } known
            ? new Dictionary<string, Uri>(known, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in parts.Where(p => p.Block.Image is { Name.Length: > 0 }).GroupBy(p => p.Block.Image!.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var name = group.First().Block.Image!.Name.Trim();
            var blocks = string.Join(", ", group.Select(p => p.Id));
            if (!addresses.TryGetValue(name, out var address))
            {
                checks.Add(new PhotoCheck(name, "Couldn't check this photo: it is not a Square photo in your image library.", false));
                continue;
            }
            var fetched = await fetcher.FetchAsync(address.OriginalString, ct);
            if (fetched.Refusal is { } refusal)
            {
                checks.Add(new PhotoCheck(name, refusal, false));
                continue;
            }
            if (fetched.Bytes!.Length > MaxImageBytes)
            {
                checks.Add(new PhotoCheck(name, $"Couldn't check this photo: it is larger than the {MaxImageBytes / (1024 * 1024)} MB the AI proofread can take.", false));
                continue;
            }

            var sha256 = Convert.ToHexStringLower(SHA256.HashData(fetched.Bytes));
            PhotoReading? reading = null;
            if (business?.PhotoReadings is { } store)
            {
                try
                {
                    reading = await store.FindAsync(sha256, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Read it again rather than fail the proofread.
                    log.LogWarning("A photo reading could not be looked up: {Error}.", ex.GetType().Name);
                }
            }
            if (reading is not null)
            {
                remembered.Add(new PlannedPhoto("", blocks, name, sha256, null, null, reading));
                checks.Add(new PhotoCheck(name, ReadBefore, true));
            }
            else if (fresh.Count >= MaxPhotos)
            {
                checks.Add(new PhotoCheck(name, $"Couldn't check this photo: the AI proofread reads at most {MaxPhotos} new photos at a time.", false));
            }
            else
            {
                fresh.Add(new PlannedPhoto($"p{fresh.Count + 1}", blocks, name, sha256, fetched.Bytes, fetched.MediaType, null));
                checks.Add(new PhotoCheck(name, ReadNow, true));
            }
        }
        return new PhotoPlan(fresh, remembered, checks);
    }

    /// <summary>
    /// The request: the instructions first, as the system prompt, marked for caching, so the part that
    /// never changes is one cached prefix; then the content, all of it in one user turn: the business as
    /// background, the offer's facts, the email block by block, each photo (its earlier reading, or the
    /// image itself), and the fixed request. Thinking is adaptive (Opus 5.5 always thinks), effort is
    /// set, and the answer is held to the findings schema. On the Anthropic API a declined request is
    /// run again server-side on the recommended model (<c>fallbacks: "default"</c>).
    /// </summary>
    internal static MessageCreateParams Request(
        Campaign campaign, IReadOnlyList<PreviewPart> parts, PhotoPlan photos, BusinessContext? business,
        ProofreadInstructions instructions, ProofreadRequestSettings settings)
    {
        var content = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = Content(campaign, parts, business) } };
        foreach (var photo in photos.Remembered)
        {
            content.Add(new BetaTextBlockParam
            {
                Text = $"<photo blocks=\"{photo.Blocks}\" name=\"{Attribute(photo.Name)}\" read-before=\"yes\">\n" +
                       $"Words in it: {photo.Reading!.Words}\nWhat it shows: {photo.Reading.Shows}\nOffers and dates: {photo.Reading.Offers}\n</photo>",
            });
        }
        foreach (var photo in photos.Fresh)
        {
            content.Add(new BetaTextBlockParam { Text = $"<photo id=\"{photo.Id}\" blocks=\"{photo.Blocks}\" name=\"{Attribute(photo.Name)}\">" });
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(photo.Bytes!), MediaType = photo.MediaType! },
            });
            content.Add(new BetaTextBlockParam { Text = "</photo>" });
        }
        content.Add(new BetaTextBlockParam { Text = instructions.Request });

        var request = new MessageCreateParams
        {
            Model = settings.Model,
            MaxTokens = settings.MaxTokens,
            System = new List<BetaTextBlockParam>
            {
                new() { Text = instructions.SystemPrompt, CacheControl = new BetaCacheControlEphemeral() },
            },
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig
            {
                Effort = settings.Effort,
                Format = new BetaJsonOutputFormat { Schema = Schema(instructions) },
            },
            Messages = [new() { Role = Role.User, Content = content }],
        };
        return settings.ServerSideFallback
            ? request with { Fallbacks = new Default(), Betas = [ServerSideFallbackBeta] }
            : request;
    }

    // The content, as text: who is sending, the offer's facts, then the email itself.
    internal static string Content(Campaign campaign, IReadOnlyList<PreviewPart> parts, BusinessContext? business)
    {
        var text = new StringBuilder();
        if (business is not null)
        {
            text.Append("<business>\n").Append("Name: ").Append(business.Name).Append('\n')
                .Append(business.Description ?? "(no description)").Append("\n</business>\n\n");
        }

        var offers = campaign.BlocksOf<OfferBlock>().ToList();
        text.Append("<tiers>\n");
        if (offers.Count == 0) text.Append("(no tiered offer)\n");
        foreach (var offer in offers)
        {
            for (var i = 0; i < offer.Offer.Tiers.Count; i++)
            {
                var tier = offer.Offer.Tiers[i];
                text.Append($"{offer.Label}, tier {i + 1}: name \"{tier.Name}\", {EditorExport.PriceText(tier.MonthlyPrice, offer.Offer.IsRecurring)}; benefits: ")
                    .Append(string.Join("; ", tier.Benefits.Select(b => b.Describe()))).Append('\n');
            }
        }
        text.Append("</tiers>\n\n<email>\n");
        text.Append("[subject]\n").Append(campaign.Subject).Append("\n\n");
        text.Append("[preheader]\n").Append(string.IsNullOrWhiteSpace(campaign.Preheader) ? "(none)" : campaign.Preheader).Append("\n\n");
        foreach (var part in parts.Where(p => p.Block.Kind != BlockKind.Spacer))
        {
            var block = part.Block;
            var widget = SquareWidgets.ByKind.TryGetValue(block.Kind, out var w) ? w.DisplayName : block.Kind.ToString();
            text.Append($"[{part.Id}] {widget}");
            if (block.Image is { } image)
                text.Append($" with the photo \"{image.Name}\"").Append(string.IsNullOrWhiteSpace(image.AltText) ? ", which has no description" : $", described as \"{image.AltText}\"");
            if (block.Url is not null) text.Append($", linking to {block.Url}");
            text.Append('\n');
            if (block.Kind != BlockKind.Image && block.Text.Length > 0) text.Append(block.Text).Append('\n');
            text.Append('\n');
        }
        text.Append("</email>");
        return text.ToString();
    }

    private static string Attribute(string s) => s.Replace("\"", "'", StringComparison.Ordinal);

    // The answer's shape: findings, each with what the page needs, and a reading of each photo sent as
    // an image. Every field is required and no other is allowed, so an answer that parses is one the
    // app can show.
    internal static IReadOnlyDictionary<string, JsonElement> Schema(ProofreadInstructions instructions)
    {
        var schema = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "findings", "photos" },
            properties = new
            {
                findings = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "severity", "category", "block", "quote", "problem", "suggestion" },
                        properties = new
                        {
                            severity = new { type = "string", @enum = new[] { ProofreadInstructions.MustFix, ProofreadInstructions.WorthALook } },
                            category = new { type = "string", @enum = instructions.Categories },
                            block = new { type = "string", description = "The block's id, such as b3, or subject or preheader." },
                            quote = new { type = "string", description = "The words at fault, copied exactly." },
                            problem = new { type = "string" },
                            suggestion = new { type = "string" },
                        },
                    },
                },
                photos = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "photo", "words", "shows", "offers" },
                        properties = new
                        {
                            photo = new { type = "string", description = "The photo's id, such as p1." },
                            words = new { type = "string", description = "Every word in the photo, exactly; empty if none." },
                            shows = new { type = "string", description = "One line on what the photo shows." },
                            offers = new { type = "string", description = "Any offer, price or date it states; empty if none." },
                        },
                    },
                },
            },
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(schema));
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    // Counts only: what the proofread cost and how it ended. Never the email, the business or the findings.
    private void LogUsage(BetaMessage response, int photosSent, int photosRemembered)
    {
        var usage = response.Usage;
        log.LogInformation(
            "AI proofread: model {Model}, stop {StopReason}, photos sent {PhotosSent}, photos from earlier readings {PhotosRemembered}; " +
            "tokens in {InputTokens}, out {OutputTokens}, cache read {CacheReadTokens}, cache written {CacheWriteTokens}.",
            response.Model.Raw(), response.StopReason?.Raw(), photosSent, photosRemembered,
            usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record Answer(
        [property: JsonPropertyName("findings")] IReadOnlyList<RawFinding?>? Findings,
        [property: JsonPropertyName("photos")] IReadOnlyList<RawReading?>? Photos);

    private sealed record RawFinding(
        string? Severity, string? Category, string? Block, string? Quote, string? Problem, string? Suggestion);

    private sealed record RawReading(string? Photo, string? Words, string? Shows, string? Offers);
}
