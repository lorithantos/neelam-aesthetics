using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neelam.Campaigns;

/// <summary>
/// Saves drafts and templates as JSON. A draft round-trips with every slot's origin, so a copied
/// benefit nobody has reviewed is still unreviewed after it is saved and opened again.
/// </summary>
public static class CampaignJson
{
    /// <summary>Written into every document so older saves can still be read after a change.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string SerializeDraft(CampaignDraft d) =>
        JsonSerializer.Serialize(new DraftDocument(
            SchemaVersion,
            d.TemplateName,
            Save(d.Subject), Save(d.Preheader), Save(d.Headline), Save(d.Greeting), Save(d.Opening),
            d.Offer is null ? null : Save(d.Offer),
            Save(d.Closing), Save(d.SignOff), Save(d.CallToAction), Save(d.Disclaimer)), Options);

    public static CampaignDraft DeserializeDraft(string json)
    {
        var doc = JsonSerializer.Deserialize<DraftDocument>(json, Options)
                  ?? throw new InvalidDataException("Empty draft document.");
        CheckVersion(doc.Schema);

        var d = new CampaignDraft
        {
            TemplateName = doc.TemplateName,
            Offer = doc.Offer is null ? null : new OfferDraft { IsRecurring = doc.Offer.IsRecurring },
        };
        Restore(d.Subject, doc.Subject);
        Restore(d.Preheader, doc.Preheader);
        Restore(d.Headline, doc.Headline);
        Restore(d.Greeting, doc.Greeting);
        Restore(d.Opening, doc.Opening);
        Restore(d.Closing, doc.Closing);
        Restore(d.SignOff, doc.SignOff);
        Restore(d.CallToAction, doc.CallToAction);
        Restore(d.Disclaimer, doc.Disclaimer);

        if (doc.Offer is { } o)
        {
            Restore(d.Offer!.Name, o.Name);
            Restore(d.Offer.Summary, o.Summary);
            Restore(d.Offer.TiersNote, o.TiersNote);
            Restore(d.Offer.TermsUrl, o.TermsUrl);
            foreach (var t in o.Tiers)
            {
                var tier = d.Offer.AddTier();
                Restore(tier.Name, t.Name);
                Restore(tier.MonthlyPrice, t.MonthlyPrice);
                foreach (var b in t.Benefits) Restore(tier.AddEmptyBenefit(), b);
            }
        }
        return d;
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
        SlotDocument<string> Headline,
        SlotDocument<string> Greeting,
        SlotDocument<IReadOnlyList<string>> Opening,
        OfferDocument? Offer,
        SlotDocument<IReadOnlyList<string>> Closing,
        SlotDocument<SignOff> SignOff,
        SlotDocument<CallToAction> CallToAction,
        SlotDocument<string> Disclaimer);

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
}
