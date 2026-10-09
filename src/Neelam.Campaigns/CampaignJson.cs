using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neelam.Campaigns;

/// <summary>
/// Saves drafts, templates, template baselines and each client's catalog, check policy and look as JSON. A draft round-trips with every slot's origin, so a copied
/// benefit nobody has reviewed is still unreviewed after it is saved and opened again.
/// </summary>
public static class CampaignJson
{
    /// <summary>
    /// Written into every document so older saves can still be read after a change. 2 since
    /// campaigns became template-defined blocks (2026-10-03); nothing had been saved as 1.
    /// </summary>
    public const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string SerializeDraft(CampaignDraft d) =>
        JsonSerializer.Serialize(new DraftDocument(
            SchemaVersion, d.TemplateName, Save(d.Subject), Save(d.Preheader),
            d.Blocks.Select(Save).ToList(),
            string.IsNullOrWhiteSpace(d.Label) ? null : d.Label.Trim()), Options);

    public static CampaignDraft DeserializeDraft(string json)
    {
        var doc = JsonSerializer.Deserialize<DraftDocument>(json, Options)
                  ?? throw new InvalidDataException("Empty draft document.");
        CheckVersion(doc.Schema);

        // Each block is rebuilt empty, the way its template would start it, and then every slot is
        // put back with its origin, so a copy nobody reviewed is still unreviewed.
        var blocks = doc.Blocks.Select(Restore).ToList();
        var d = new CampaignDraft(doc.TemplateName, blocks) { Label = doc.Label };
        Restore(d.Subject, doc.Subject);
        Restore(d.Preheader, doc.Preheader);
        return d;
    }

    private static BlockDocument Save(BlockDraft b) => b switch
    {
        ValueBlockDraft<string> v => new(b.Label, b.Type, b.Required, Text: Save(v.Value)),
        ValueBlockDraft<IReadOnlyList<string>> v => new(b.Label, b.Type, b.Required, Paragraphs: Save(v.Value)),
        ValueBlockDraft<CallToAction> v => new(b.Label, b.Type, b.Required, Action: Save(v.Value)),
        ValueBlockDraft<SignOff> v => new(b.Label, b.Type, b.Required, SignOff: Save(v.Value)),
        ValueBlockDraft<HeaderContent> v => new(b.Label, b.Type, b.Required, Header: Save(v.Value)),
        ValueBlockDraft<ImageRef> v => new(b.Label, b.Type, b.Required, Image: Save(v.Value)),
        SpacerBlockDraft => new(b.Label, b.Type, b.Required),
        OfferBlockDraft o => new(b.Label, b.Type, b.Required, Offer: Save(o.Offer), Marker: o.Marker),
        _ => throw new InvalidOperationException($"No saved form for a {b.Type} block."),
    };

    private static BlockDraft Restore(BlockDocument doc)
    {
        var block = BlockDraft.From(new TemplateBlock(doc.Label, doc.Type, doc.Required, Marker: doc.Marker));
        switch (block)
        {
            case ValueBlockDraft<string> v: Restore(v.Value, doc.Text); break;
            case ValueBlockDraft<IReadOnlyList<string>> v: Restore(v.Value, doc.Paragraphs); break;
            case ValueBlockDraft<CallToAction> v: Restore(v.Value, doc.Action); break;
            case ValueBlockDraft<SignOff> v: Restore(v.Value, doc.SignOff); break;
            case ValueBlockDraft<HeaderContent> v: Restore(v.Value, doc.Header); break;
            case ValueBlockDraft<ImageRef> v: Restore(v.Value, doc.Image); break;
            case OfferBlockDraft o when doc.Offer is { } saved: Restore(o.Offer, saved); break;
        }
        return block;
    }

    private static void Restore(OfferDraft offer, OfferDocument o)
    {
        offer.IsRecurring = o.IsRecurring;
        Restore(offer.Name, o.Name);
        Restore(offer.Summary, o.Summary);
        Restore(offer.TiersNote, o.TiersNote);
        Restore(offer.TermsUrl, o.TermsUrl);
        foreach (var t in o.Tiers)
        {
            var tier = offer.AddTier();
            Restore(tier.Name, t.Name);
            Restore(tier.MonthlyPrice, t.MonthlyPrice);
            foreach (var b in t.Benefits) Restore(tier.AddEmptyBenefit(), b);
        }
    }

    public static string SerializeTemplate(CampaignTemplate t) =>
        JsonSerializer.Serialize(new TemplateDocument(SchemaVersion, t), Options);

    public static CampaignTemplate DeserializeTemplate(string json)
    {
        var doc = JsonSerializer.Deserialize<TemplateDocument>(json, Options)
                  ?? throw new InvalidDataException("Empty template document.");
        CheckVersion(doc.Schema);
        return doc.Template;
    }

    public static string SerializeCatalog(ClientCatalog c) =>
        JsonSerializer.Serialize(new CatalogDocument(SchemaVersion, c.Entries), Options);

    public static ClientCatalog DeserializeCatalog(string json)
    {
        var doc = JsonSerializer.Deserialize<CatalogDocument>(json, Options)
                  ?? throw new InvalidDataException("Empty catalog document.");
        CheckVersion(doc.Schema);
        return new ClientCatalog(doc.Entries);
    }

    /// <summary>
    /// Typed benefits as compact JSON, each with its kind, the same shape a draft saves them in: what a
    /// known benefit or known tier keeps in its table row.
    /// </summary>
    public static string SerializeBenefits(IReadOnlyList<Benefit> benefits) =>
        JsonSerializer.Serialize(benefits, Compact);

    public static IReadOnlyList<Benefit> DeserializeBenefits(string json) =>
        JsonSerializer.Deserialize<List<Benefit>>(json, Compact)
        ?? throw new InvalidDataException("Empty benefit list.");

    private static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static string SerializePolicy(CampaignPolicy p) =>
        JsonSerializer.Serialize(new PolicyDocument(SchemaVersion, p), Options);

    public static CampaignPolicy DeserializePolicy(string json)
    {
        var doc = JsonSerializer.Deserialize<PolicyDocument>(json, Options)
                  ?? throw new InvalidDataException("Empty policy document.");
        CheckVersion(doc.Schema);
        return doc.Policy;
    }

    public static string SerializeLook(ClientLook l) =>
        JsonSerializer.Serialize(new LookDocument(SchemaVersion, l.AccentColour), Options);

    public static ClientLook DeserializeLook(string json)
    {
        var doc = JsonSerializer.Deserialize<LookDocument>(json, Options)
                  ?? throw new InvalidDataException("Empty look document.");
        CheckVersion(doc.Schema);
        return new ClientLook(doc.AccentColour);
    }

    public static string SerializeBaseline(TemplateBaseline b) =>
        JsonSerializer.Serialize(new BaselineDocument(SchemaVersion, b.Parts), Options);

    public static TemplateBaseline DeserializeBaseline(string json)
    {
        var doc = JsonSerializer.Deserialize<BaselineDocument>(json, Options)
                  ?? throw new InvalidDataException("Empty baseline document.");
        CheckVersion(doc.Schema);
        return new TemplateBaseline(doc.Parts ?? throw new InvalidDataException("A baseline document without its parts."));
    }

    private static void CheckVersion(int schema)
    {
        if (schema != SchemaVersion)
            throw new InvalidDataException($"Saved with schema {schema}; this version reads {SchemaVersion}.");
    }

    private static SlotDocument<T> Save<T>(Slot<T> s) =>
        new(s.Origin, s.HasValue ? s.Value : default, s.CopiedFrom);

    private static void Restore<T>(Slot<T> slot, SlotDocument<T>? saved)
    {
        if (saved is null || saved.Origin == Origin.Empty) return;
        if (saved.Value is null)
            throw new InvalidDataException($"A {saved.Origin} value was saved without its content.");
        slot.Restore(saved.Origin, saved.Value, saved.CopiedFrom);
    }

    private static OfferDocument Save(OfferDraft o) => new(
        Save(o.Name), Save(o.Summary), Save(o.TiersNote), Save(o.TermsUrl), o.IsRecurring,
        o.Tiers.Select(t => new TierDocument(
            Save(t.Name), Save(t.MonthlyPrice), t.Benefits.Select(Save).ToList())).ToList());

    private sealed record SlotDocument<T>(Origin Origin, T? Value, string? CopiedFrom);

    private sealed record DraftDocument(
        int Schema,
        string? TemplateName,
        SlotDocument<string> Subject,
        SlotDocument<string> Preheader,
        IReadOnlyList<BlockDocument> Blocks,
        // The client's own label (2026-10-09). Optional, and left out when there is none, so drafts
        // saved before it read as unlabelled and the schema stays 2.
        string? Label = null);

    // One saved block: which of the value fields is set follows from its type.
    private sealed record BlockDocument(
        string Label,
        BlockType Type,
        bool Required,
        SlotDocument<string>? Text = null,
        SlotDocument<IReadOnlyList<string>>? Paragraphs = null,
        SlotDocument<CallToAction>? Action = null,
        SlotDocument<SignOff>? SignOff = null,
        OfferDocument? Offer = null,
        SlotDocument<HeaderContent>? Header = null,
        SlotDocument<ImageRef>? Image = null,
        string? Marker = null);

    private sealed record OfferDocument(
        SlotDocument<string> Name,
        SlotDocument<string> Summary,
        SlotDocument<string> TiersNote,
        SlotDocument<Uri> TermsUrl,
        bool IsRecurring,
        IReadOnlyList<TierDocument> Tiers);

    private sealed record TierDocument(
        SlotDocument<string> Name,
        SlotDocument<decimal> MonthlyPrice,
        IReadOnlyList<SlotDocument<Benefit>> Benefits);

    private sealed record TemplateDocument(int Schema, CampaignTemplate Template);

    private sealed record CatalogDocument(int Schema, IReadOnlyList<CatalogEntry> Entries);

    private sealed record PolicyDocument(int Schema, CampaignPolicy Policy);

    private sealed record LookDocument(int Schema, string? AccentColour);

    // Parts are block types by name ("signOff"), so an empty list is an empty baseline, not a missing one.
    private sealed record BaselineDocument(int Schema, IReadOnlyList<BlockType>? Parts);
}
