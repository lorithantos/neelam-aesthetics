namespace Neelam.Campaigns.Storage;

/// <summary>A photo in a client's image library.</summary>
/// <param name="Name">How blocks refer to it (<see cref="ImageRef.Name"/>), unique ignoring case.</param>
/// <param name="AltText">What a reader gets when images do not load, unless a block says otherwise.</param>
public sealed record LibraryImage(
    string Name, string ContentType, string? AltText, DateTimeOffset AddedAt, string BlobName);

/// <summary>
/// A client's photos, for templates and campaigns to choose from: one blob each under
/// <c>images/</c> in the client's own container, so they are the client's data like everything
/// else there. Square has no API, so a person places each photo in Square's editor; the library is
/// where the export finds the file to hand them. Deleting a photo is final and leaves no record.
/// </summary>
public sealed class ImageLibrary(IBlobBackend clientContainer, TimeProvider clock)
{
    private const string Prefix = "images/";
    private const string NameKey = "name";
    private const string AltKey = "alt";
    private const string AddedKey = "added";

    /// <summary>The formats email clients show reliably.</summary>
    public static readonly IReadOnlyList<string> AcceptedTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"];

    /// <summary>
    /// A judgment, not a Square limit: comfortably above an email photo (Neelam's were 1024 px
    /// wide), and small enough that an upload never strains the app.
    /// </summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    public async Task<LibraryImage> AddAsync(
        string name, BinaryData content, string contentType, string? altText = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A photo needs a name, so blocks can refer to it.", nameof(name));
        name = name.Trim();
        if (!AcceptedTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"A {contentType} file cannot go in an email; use {string.Join(", ", AcceptedTypes)}.", nameof(contentType));
        if (content.ToMemory().Length > MaxBytes)
            throw new ArgumentException($"The photo is larger than {MaxBytes / (1024 * 1024)} MB.", nameof(content));
        if ((await ListAsync(ct)).Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The library already has a photo called \"{name}\".");

        var added = clock.GetUtcNow();
        var metadata = new Dictionary<string, string>
        {
            [NameKey] = Uri.EscapeDataString(name),
            [AddedKey] = SaveStamp.Of(added),
        };
        if (!string.IsNullOrWhiteSpace(altText)) metadata[AltKey] = Uri.EscapeDataString(altText.Trim());

        var blobName = BlobName(name);
        if (!await clientContainer.TryCreateAsync(blobName, content, contentType, metadata, ct))
            throw new InvalidOperationException($"The library already has a photo called \"{name}\".");
        return new LibraryImage(name, contentType, altText?.Trim(), added, blobName);
    }

    /// <summary>Every photo, by name.</summary>
    public async Task<IReadOnlyList<LibraryImage>> ListAsync(CancellationToken ct = default)
    {
        var images = new List<LibraryImage>();
        await foreach (var blob in clientContainer.ListAsync(Prefix, ct))
        {
            if (!blob.Metadata.TryGetValue(NameKey, out var name)) continue;
            var added = blob.Metadata.TryGetValue(AddedKey, out var stamp) && SaveStamp.TryRead($"{stamp}.json", out var at)
                ? at
                : DateTimeOffset.MinValue;
            var alt = blob.Metadata.TryGetValue(AltKey, out var a) ? Uri.UnescapeDataString(a) : null;
            images.Add(new LibraryImage(Uri.UnescapeDataString(name), "", alt, added, blob.Name));
        }
        return images.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The photo's bytes and type, for the editor to show and the export to hand over.</summary>
    public Task<Janet.Azure.Storage.BlobContent> ReadAsync(string name, CancellationToken ct = default) =>
        clientContainer.ReadAsync(BlobName(name.Trim()), ct);

    /// <summary>Permanently deletes a photo. Blocks that named it will no longer find it.</summary>
    public Task<bool> DeleteAsync(string name, CancellationToken ct = default) =>
        clientContainer.DeleteAsync(BlobName(name.Trim()), ct);

    // The name, escaped: reversible, and a "/" in a name can never make a path.
    private static string BlobName(string name) => Prefix + Uri.EscapeDataString(name);
}
