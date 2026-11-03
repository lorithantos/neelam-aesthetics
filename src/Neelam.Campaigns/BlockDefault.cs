using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neelam.Campaigns;

/// <summary>
/// What a template block gives each new campaign to start with, which the campaign can change (owner,
/// 2026-11-03: "the real problem is that there is no "default text" that prevents them from having to
/// select new each time even if it is different"). Any block but a spacer may have it, an offer and a
/// promotion included ("Offers can also have defaults. This makes them drop in if desired, but does not
/// make that a requirement"), and for those two it may be only part of one, such as a promotion's lines
/// without its dates, which each campaign then fills in.
/// </summary>
/// <remarks>
/// Kept as the block's content in the shape a draft saves it (<see cref="CampaignJson"/>), every value as
/// typed (<see cref="Origin.Entered"/>), and nothing about where the block sits: its label, type, whether
/// it is required, an offer's marker and whether it recurs are the template block's. The same content
/// is always the same text, however its values came to be there, so whether a campaign still holds it
/// is a comparison of text (<see cref="IsHeldBy"/>). Written into the template's JSON as an object.
/// </remarks>
[JsonConverter(typeof(Json))]
public sealed record BlockDefault
{
    private BlockDefault(string content) => Content = content;

    // The content as compact JSON, the same for the same content.
    private string Content { get; }

    /// <summary>The content <paramref name="block"/> holds, to start each campaign with; null when it holds nothing.</summary>
    internal static BlockDefault? Of(BlockDraft block) =>
        block.HasContent ? new BlockDefault(CampaignJson.SerializeBlockContent(block)) : null;

    /// <summary>Puts this content into <paramref name="block"/>, a block of its type with nothing in it, every value as if she had typed it.</summary>
    internal void FillInto(BlockDraft block) => CampaignJson.RestoreBlockContent(block, Content);

    /// <summary>
    /// Whether <paramref name="block"/> holds exactly this content, nothing changed, added or taken out;
    /// where its values came from (typed, copied, confirmed) does not count.
    /// </summary>
    internal bool IsHeldBy(BlockDraft block) => block.HasContent && CampaignJson.SerializeBlockContent(block) == Content;

    // The content as it stands in the template's JSON: an object, read back to the same text.
    private sealed class Json : JsonConverter<BlockDefault>
    {
        public override BlockDefault Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("A block's content to start with is an object.");
            return new BlockDefault(CampaignJson.CanonicalBlockContent(document.RootElement.GetRawText()));
        }

        public override void Write(Utf8JsonWriter writer, BlockDefault value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.Content, skipInputValidation: true);
    }
}
