using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// A save's AI proofread as its blob holds it, version 1: when it ran, each finding's severity
/// ("must-fix" or "worth-a-look", as the page says them), rule, place, message and quoted text, and
/// how each photo was dealt with.
/// </summary>
internal static class ProofreadJson
{
    private const int Version = 1;

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(ProofreadRecord record) => JsonSerializer.Serialize(
        new Document(
            Version,
            record.At,
            record.Findings.Select(f => new StoredFinding(
                f.Severity == Severity.Blocker ? MustFix : WorthALook, f.Rule, f.Location, f.Message, f.Excerpt)).ToList(),
            record.Photos.Select(p => new StoredPhoto(p.Name, p.Note, p.Checked)).ToList()),
        Options);

    /// <exception cref="InvalidOperationException">The blob is not a proofread this app wrote.</exception>
    public static ProofreadRecord Deserialize(string json)
    {
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("A kept proofread could not be read.", ex);
        }
        if (document is not { Version: Version, Findings: { } findings })
            throw new InvalidOperationException("A kept proofread is not one this app wrote.");
        return new ProofreadRecord(
            document.At,
            findings.Select(f => new Finding(
                f.Severity switch
                {
                    MustFix => Severity.Blocker,
                    WorthALook => Severity.Warning,
                    _ => throw new InvalidOperationException("A kept proofread has a finding of no known severity."),
                },
                f.Rule, f.Location, f.Message, f.Excerpt)).ToList(),
            (document.Photos ?? []).Select(p => new PhotoCheck(p.Name, p.Note, p.Checked)).ToList());
    }

    private const string MustFix = "must-fix";
    private const string WorthALook = "worth-a-look";

    private sealed record Document(int Version, DateTimeOffset At, IReadOnlyList<StoredFinding>? Findings, IReadOnlyList<StoredPhoto>? Photos);

    private sealed record StoredFinding(string Severity, string Rule, string Location, string Message, string? Excerpt);

    private sealed record StoredPhoto(string Name, string Note, bool Checked);
}

/// <summary>
/// A client's photo readings (<see cref="IPhotoReadings"/>) in her own container, one blob each at
/// <c>proofread/images/{sha256}.json</c>: what the AI read in the photo whose bytes have that hash.
/// Created once, never replaced; a changed photo has a new hash, so it is read again.
/// </summary>
internal sealed class PhotoReadingStore(IBlobBackend container) : IPhotoReadings
{
    internal const string Prefix = "proofread/images/";

    public async Task<PhotoReading?> FindAsync(string sha256, CancellationToken cancellationToken = default)
    {
        var name = Name(sha256);
        await foreach (var blob in container.ListAsync(name, cancellationToken))
        {
            if (blob.Name != name) continue;
            try
            {
                return JsonSerializer.Deserialize<PhotoReading>(await container.ReadTextAsync(name, cancellationToken), ProofreadJson.Options);
            }
            catch (JsonException)
            {
                // Not one this app wrote: read the photo again rather than trust it.
                return null;
            }
        }
        return null;
    }

    public async Task KeepAsync(string sha256, PhotoReading reading, CancellationToken cancellationToken = default) =>
        await container.TryCreateTextAsync(Name(sha256), JsonSerializer.Serialize(reading, ProofreadJson.Options),
            new Dictionary<string, string>(), cancellationToken);

    // A hash, exactly: nothing else can make a blob name here.
    private static string Name(string sha256) =>
        sha256.Length == 64 && sha256.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            ? $"{Prefix}{sha256}.json"
            : throw new ArgumentException("A photo reading is kept by the SHA-256 of its bytes, as 64 lowercase hex digits.", nameof(sha256));
}
