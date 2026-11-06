using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neelam.Campaigns;

/// <summary>
/// Saves drafts, templates, template baselines, tier-name ladders and each client's catalog, check policy and look as JSON. A draft round-trips with every slot's origin, so a copied
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

    public static string SerializeDraft(CampaignDraft d) => SerializeDraft(d, content: false);

    /// <summary>
    /// The draft as an approval sees it: its JSON without her label, which is hers alone and never in
    /// the email, and without the template's id, version and name, which say where the blocks came from
    /// and not what they hold (owner, 2026-10-31; the name since 2026-11-01: "renaming a template should
    /// not change the campaigns"). Not for saving: it does not read back as the draft.
    /// </summary>
    public static string SerializeContent(CampaignDraft d) => SerializeDraft(d, content: true);

    private static string SerializeDraft(CampaignDraft d, bool content) =>
        JsonSerializer.Serialize(new DraftDocument(
            SchemaVersion, content ? null : d.TemplateName, Save(d.Subject), Save(d.Preheader),
            d.Blocks.Select(b => Save(b, content)).ToList(),
            content || string.IsNullOrWhiteSpace(d.Label) ? null : d.Label.Trim(),
            content ? null : d.TemplateId,
            content ? null : d.TemplateVersion), Options);

    public static CampaignDraft DeserializeDraft(string json) => DeserializeDraft(json, null, null);

    /// <summary>
    /// A draft, with the template it was linked to afterwards (<paramref name="linkedTemplate"/>, from
    /// outside its JSON) when its JSON records none; the JSON's own template wins.
    /// </summary>
    public static CampaignDraft DeserializeDraft(string json, Guid? linkedTemplate, DateTimeOffset? linkedVersion)
    {
        var doc = JsonSerializer.Deserialize<DraftDocument>(json, Options)
                  ?? throw new InvalidDataException("Empty draft document.");
        CheckVersion(doc.Schema);

        // Each block is rebuilt empty, the way its template would start it, and then every slot is
        // put back with its origin, so a copy nobody reviewed is still unreviewed.
        var blocks = doc.Blocks.Select(Restore).ToList();
        var linked = doc.TemplateId is null && linkedTemplate is not null;
        var d = new CampaignDraft(doc.TemplateName, blocks)
        {
            Label = doc.Label,
            TemplateId = linked ? linkedTemplate : doc.TemplateId,
            TemplateVersion = linked ? linkedVersion : doc.TemplateVersion,
        };
        Restore(d.Subject, doc.Subject);
        Restore(d.Preheader, doc.Preheader);
        return d;
    }

    private static BlockDocument Save(BlockDraft b, bool content) => b switch
    {
        ValueBlockDraft<string> v => new(b.Label, b.Type, b.Required, Text: Save(v.Value)),
        ValueBlockDraft<IReadOnlyList<string>> v => new(b.Label, b.Type, b.Required, Paragraphs: Save(v.Value)),
        ValueBlockDraft<CallToAction> v => new(b.Label, b.Type, b.Required, Action: Save(v.Value)),
        ValueBlockDraft<SignOff> v => new(b.Label, b.Type, b.Required, SignOff: Save(v.Value)),
        ValueBlockDraft<HeaderContent> v => new(b.Label, b.Type, b.Required, Header: Save(v.Value)),
        ValueBlockDraft<ImageRef> v => new(b.Label, b.Type, b.Required, Image: Save(v.Value)),
        SpacerBlockDraft => new(b.Label, b.Type, b.Required),
        OfferBlockDraft o => new(b.Label, b.Type, b.Required, Offer: Save(o.Offer), Marker: o.Marker),
        PromotionBlockDraft p => new(b.Label, b.Type, b.Required, Promotion: Save(p.Promotion, content)),
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
            case PromotionBlockDraft p when doc.Promotion is { } saved: Restore(p.Promotion, saved); break;
        }
        return block;
    }

    private static void Restore(PromotionDraft promotion, PromotionDocument p)
    {
        Restore(promotion.Name, p.Name);
        Restore(promotion.Summary, p.Summary);
        Restore(promotion.Audience, p.Audience);
        Restore(promotion.Starts, p.Starts);
        Restore(promotion.Ends, p.Ends);
        foreach (var line in p.Lines)
        {
            switch (line.Kind)
            {
                case PromotionLineKind.Treatment:
                    var priced = promotion.AddTreatment();
                    Restore(priced.Treatment, line.Treatment);
                    Restore(priced.Price, line.Price);
                    Restore(priced.RegularPrice, line.RegularPrice);
                    priced.TreatmentId = line.TreatmentId;
                    break;
                case PromotionLineKind.Benefit:
                    Restore(promotion.AddBenefit().Benefit, line.Benefit);
                    break;
                default:
                    throw new InvalidDataException($"A promotion line of no known kind ('{line.Kind}').");
            }
        }
    }

    // The link to a known treatment says where a line came from, not what the email says, so an
    // approval's content leaves it out.
    private static PromotionDocument Save(PromotionDraft p, bool content) => new(
        Save(p.Name), Save(p.Summary), Save(p.Audience), Save(p.Starts), Save(p.Ends),
        p.Lines.Select(l => l switch
        {
            PricedTreatmentDraft t => new PromotionLineDocument(PromotionLineKind.Treatment,
                Treatment: Save(t.Treatment), Price: Save(t.Price), RegularPrice: Save(t.RegularPrice),
                TreatmentId: content ? null : t.TreatmentId),
            BenefitLineDraft b => new PromotionLineDocument(PromotionLineKind.Benefit, Benefit: Save(b.Benefit)),
            _ => throw new InvalidOperationException("No saved form for this promotion line."),
        }).ToList());

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

    /// <summary>
    /// One block's content alone, as a template's content to start with keeps it (<see cref="BlockDefault"/>):
    /// compact, every value written as typed (<see cref="Origin.Entered"/>), an offer never marked as
    /// recurring, and nothing about where the block sits. The same content is always the same text.
    /// </summary>
    internal static string SerializeBlockContent(BlockDraft b) => JsonSerializer.Serialize(AsTyped(ContentOf(Save(b, content: false))), Compact);

    /// <summary>
    /// Puts content written by <see cref="SerializeBlockContent"/> into a block with nothing in it, every
    /// value as typed. An offer keeps whether it recurs, which is the template block's to say.
    /// </summary>
    internal static void RestoreBlockContent(BlockDraft into, string json)
    {
        var doc = ReadContent(json);
        switch (into)
        {
            case ValueBlockDraft<string> v: Restore(v.Value, doc.Text); break;
            case ValueBlockDraft<IReadOnlyList<string>> v: Restore(v.Value, doc.Paragraphs); break;
            case ValueBlockDraft<CallToAction> v: Restore(v.Value, doc.Action); break;
            case ValueBlockDraft<SignOff> v: Restore(v.Value, doc.SignOff); break;
            case ValueBlockDraft<HeaderContent> v: Restore(v.Value, doc.Header); break;
            case ValueBlockDraft<ImageRef> v: Restore(v.Value, doc.Image); break;
            case OfferBlockDraft o when doc.Offer is { } saved:
                var recurring = o.Offer.IsRecurring;
                Restore(o.Offer, saved);
                o.Offer.IsRecurring = recurring;
                break;
            case PromotionBlockDraft p when doc.Promotion is { } saved: Restore(p.Promotion, saved); break;
        }
    }

    /// <summary>Block content as read from a template's JSON, written again as <see cref="SerializeBlockContent"/> writes it.</summary>
    /// <exception cref="InvalidDataException">It is not block content.</exception>
    internal static string CanonicalBlockContent(string json) => JsonSerializer.Serialize(AsTyped(ReadContent(json)), Compact);

    private static ContentDocument ReadContent(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ContentDocument>(json, Options) ?? throw new InvalidDataException("Empty block content.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not a block's content: {ex.Message}", ex);
        }
    }

    private static ContentDocument ContentOf(BlockDocument b) =>
        new(b.Text, b.Paragraphs, b.Action, b.SignOff, b.Header, b.Image, b.Offer, b.Promotion);

    // Every value as typed, whatever its origin: what a template starts a campaign with is as if she had
    // typed it, and two blocks holding the same values hold the same content.
    private static ContentDocument AsTyped(ContentDocument c) => new(
        AsTyped(c.Text), AsTyped(c.Paragraphs), AsTyped(c.Action), AsTyped(c.SignOff), AsTyped(c.Header), AsTyped(c.Image),
        c.Offer is { } o
            ? new OfferDocument(AsTyped(o.Name)!, AsTyped(o.Summary)!, AsTyped(o.TiersNote)!, AsTyped(o.TermsUrl)!, false,
                o.Tiers.Select(t => new TierDocument(AsTyped(t.Name)!, AsTyped(t.MonthlyPrice)!, t.Benefits.Select(x => AsTyped(x)!).ToList())).ToList())
            : null,
        c.Promotion is { } p
            ? new PromotionDocument(AsTyped(p.Name)!, AsTyped(p.Summary)!, AsTyped(p.Audience)!, AsTyped(p.Starts)!, AsTyped(p.Ends)!,
                p.Lines.Select(l => new PromotionLineDocument(l.Kind, AsTyped(l.Treatment), AsTyped(l.Price), AsTyped(l.RegularPrice), AsTyped(l.Benefit), l.TreatmentId)).ToList())
            : null);

    private static SlotDocument<T>? AsTyped<T>(SlotDocument<T>? s) =>
        s is null ? null
        : s.Origin == Origin.Empty || s.Value is null ? new SlotDocument<T>(Origin.Empty, default, null)
        : new SlotDocument<T>(Origin.Entered, s.Value, null);

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

    /// <summary>A saved promotion's lines as compact JSON, each with its kind, as a known item keeps them in its row.</summary>
    public static string SerializePromotionLines(IReadOnlyList<KnownPromotionLine> lines) =>
        JsonSerializer.Serialize(lines, Compact);

    public static IReadOnlyList<KnownPromotionLine> DeserializePromotionLines(string json) =>
        JsonSerializer.Deserialize<List<KnownPromotionLine>>(json, Compact)
        ?? throw new InvalidDataException("Empty promotion line list.");

    private static readonly JsonSerializerOptions Compact =new(Options) { WriteIndented = false };

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
        JsonSerializer.Serialize(
            new LookDocument(
                SchemaVersion, l.AccentColour, l.PageBackground, l.Surface, l.Border, l.Text, l.MutedText,
                l.IsStandard ? true : null),
            Options);

    /// <summary>
    /// A look as saved; one saved when the accent was its only colour reads with every other colour
    /// the standard one. A colour that is not #RRGGBB is refused (<see cref="ArgumentException"/>), so a
    /// document changed by hand never reaches a page's style. <c>"standard": true</c> is
    /// <see cref="ClientLook.Standard"/>, which holds no colours; one that names a colour as well is
    /// refused (<see cref="InvalidDataException"/>). A reader from before the marker sees a look with no
    /// colours, which is drawn the same.
    /// </summary>
    public static ClientLook DeserializeLook(string json)
    {
        var doc = JsonSerializer.Deserialize<LookDocument>(json, Options)
                  ?? throw new InvalidDataException("Empty look document.");
        CheckVersion(doc.Schema);
        var look = new ClientLook(doc.AccentColour, doc.PageBackground, doc.Surface, doc.Border, doc.Text, doc.MutedText);
        if (doc.Standard != true) return look;
        return look == ClientLook.Default
            ? ClientLook.Standard
            : throw new InvalidDataException("The standard look holds no colours of its own.");
    }

    /// <summary>
    /// A baseline as saved. <see cref="TemplateBaseline.FollowStandard"/> is <c>"standard": true</c> with no
    /// parts, the way the look's standard version is written.
    /// </summary>
    public static string SerializeBaseline(TemplateBaseline b) =>
        JsonSerializer.Serialize(
            b.FollowsStandard ? new BaselineDocument(SchemaVersion, null, true) : new BaselineDocument(SchemaVersion, b.Parts),
            Options);

    /// <summary>
    /// A baseline as saved; <c>"standard": true</c> is <see cref="TemplateBaseline.FollowStandard"/>, and one
    /// that also lists parts is refused (<see cref="InvalidDataException"/>), as is one with neither.
    /// </summary>
    public static TemplateBaseline DeserializeBaseline(string json)
    {
        var doc = JsonSerializer.Deserialize<BaselineDocument>(json, Options)
                  ?? throw new InvalidDataException("Empty baseline document.");
        CheckVersion(doc.Schema);
        if (doc.Standard == true)
            return doc.Parts is null
                ? TemplateBaseline.FollowStandard
                : throw new InvalidDataException("The standard baseline's version lists no parts of its own.");
        return new TemplateBaseline(doc.Parts ?? throw new InvalidDataException("A baseline document without its parts."));
    }

    /// <summary>A set of ladders as saved; <see cref="TierLadders.FollowStandard"/> is <c>"standard": true</c> with no ladders.</summary>
    public static string SerializeLadders(TierLadders l) =>
        JsonSerializer.Serialize(
            l.FollowsStandard
                ? new LaddersDocument(SchemaVersion, null, true)
                : new LaddersDocument(SchemaVersion, l.Ladders.Select(x => new LadderDocument(x.Name, x.Words, x.Ordered)).ToList()),
            Options);

    /// <summary>
    /// A set of ladders as saved; <c>"standard": true</c> is <see cref="TierLadders.FollowStandard"/>, and one
    /// that also lists ladders is refused.
    /// </summary>
    /// <exception cref="InvalidDataException">The document is empty, of another schema, or not a valid set of ladders.</exception>
    public static TierLadders DeserializeLadders(string json)
    {
        var doc = JsonSerializer.Deserialize<LaddersDocument>(json, Options)
                  ?? throw new InvalidDataException("Empty ladders document.");
        CheckVersion(doc.Schema);
        if (doc.Standard == true)
            return doc.Ladders is null
                ? TierLadders.FollowStandard
                : throw new InvalidDataException("The standard ladders' version lists no ladders of its own.");
        if (doc.Ladders is null) throw new InvalidDataException("A ladders document without its ladders.");
        try
        {
            return new TierLadders(doc.Ladders.Select(x => new TierLadder(
                x.Name ?? "", x.Words ?? throw new InvalidDataException("A ladder without its words."), x.Ordered)));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
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
        string? Label = null,
        // The template and its version the blocks came from (2026-10-31). Optional in the same way: a
        // draft saved before has neither, and reads as having no template attached.
        Guid? TemplateId = null,
        DateTimeOffset? TemplateVersion = null);

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
        string? Marker = null,
        // A promotion block's values (2026-11-02). Optional like every block's: a draft saved before has
        // no promotion block, so nothing already stored changes and the schema stays 2.
        PromotionDocument? Promotion = null);

    // A block's content alone, as a template's content to start with is kept (2026-11-03): the value
    // fields of a saved block, which of them is set following from the block's type.
    private sealed record ContentDocument(
        SlotDocument<string>? Text = null,
        SlotDocument<IReadOnlyList<string>>? Paragraphs = null,
        SlotDocument<CallToAction>? Action = null,
        SlotDocument<SignOff>? SignOff = null,
        SlotDocument<HeaderContent>? Header = null,
        SlotDocument<ImageRef>? Image = null,
        OfferDocument? Offer = null,
        PromotionDocument? Promotion = null);

    private sealed record PromotionDocument(
        SlotDocument<string> Name,
        SlotDocument<string> Summary,
        SlotDocument<string> Audience,
        SlotDocument<DateOnly> Starts,
        SlotDocument<DateOnly> Ends,
        IReadOnlyList<PromotionLineDocument> Lines);

    // One line: which fields are set follows from its kind.
    private sealed record PromotionLineDocument(
        PromotionLineKind Kind,
        SlotDocument<string>? Treatment = null,
        SlotDocument<decimal>? Price = null,
        SlotDocument<decimal>? RegularPrice = null,
        SlotDocument<Benefit>? Benefit = null,
        string? TreatmentId = null);

    private enum PromotionLineKind
    {
        Treatment,
        Benefit,
    }

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

    // Every colour but the accent arrived on 2026-10-09; a document from before has only the accent.
    // Standard is true only on the version "Use the standard look" saves, and absent otherwise.
    private sealed record LookDocument(
        int Schema, string? AccentColour, string? PageBackground = null, string? Surface = null, string? Border = null,
        string? Text = null, string? MutedText = null, bool? Standard = null);

    // Parts are block types by name ("signOff"), so an empty list is an empty baseline, not a missing one.
    // Standard is true, with no parts, only on the version "Use the standard baseline" saves (2026-10-11).
    private sealed record BaselineDocument(int Schema, IReadOnlyList<BlockType>? Parts, bool? Standard = null);

    // Words lowest first when ordered; an empty list of ladders is a client with none, not a missing one.
    // Standard is true, with no ladders, only on the version "Use the standard ladders" saves (2026-10-11).
    private sealed record LaddersDocument(int Schema, IReadOnlyList<LadderDocument>? Ladders, bool? Standard = null);

    private sealed record LadderDocument(string? Name, IReadOnlyList<string>? Words, bool Ordered);
}
