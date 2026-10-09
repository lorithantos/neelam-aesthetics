namespace Neelam.Campaigns.Storage;

/// <summary>A photo in a client's image library.</summary>
/// <param name="Name">How blocks refer to it (<see cref="ImageRef.Name"/>), unique ignoring case.</param>
/// <param name="AltText">What a reader gets when images do not load, unless a block says otherwise.</param>
/// <param name="SquareUrl">
/// For a photo hosted on Square rather than uploaded: its address there, query string and all, since
/// Square crops and sizes an image through it. Null for an upload.
/// </param>
public sealed record LibraryImage(
    string Name, string ContentType, string? AltText, DateTimeOffset AddedAt, string BlobName, Uri? SquareUrl = null)
{
    /// <summary>Square holds the image; the library holds only its name and address.</summary>
    public bool IsOnSquare => SquareUrl is not null;
}

/// <summary>
/// A client's photos, for templates and campaigns to choose from: one blob each under
/// <c>images/</c> in the client's own container, so they are the client's data like everything
/// else there. Square has no API, so a person places each photo in Square's editor; the library is
/// where the export finds the file to hand them. Deleting a photo is final and leaves no record.
/// </summary>
/// <remarks>
/// A photo is either uploaded (its bytes in the blob) or <b>hosted on Square</b> (owner,
/// 2026-10-09): the client's images live in Square's own library, which she picks from when she
/// pastes the email in, so the library can reference Square's copy instead of holding one, and the
/// preview can never drift from hers. A Square photo's blob is empty: its name and address are its
/// metadata, as an upload's name and alt text are. Deleting one removes only that reference here;
/// nothing at Square is touched, and nothing here could touch it.
/// </remarks>
public sealed class ImageLibrary(IBlobBackend clientContainer, TimeProvider clock)
{
    private const string Prefix = "images/";
    private const string NameKey = "name";
    private const string AltKey = "alt";
    private const string AddedKey = "added";
    private const string SquareKey = "square";

    /// <summary>What a preview and a photo field say for a name the library does not hold.</summary>
    public const string NotInLibrary = "There is no photo by this name in the image library yet.";

    /// <summary>The formats email clients show reliably.</summary>
    public static readonly IReadOnlyList<string> AcceptedTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"];

    /// <summary>
    /// Where Square serves the images in a seller's library, as seen in the addresses Square gives
    /// out. A Square photo's address must be on one of these, exactly; any other host is refused.
    /// </summary>
    public static readonly IReadOnlyList<string> SquareHosts =
    [
        "postoffice-production-f.squarecdn.com",
        "square-web-production-f.squarecdn.com",
        "square-postoffice-production.s3.amazonaws.com",
    ];

    /// <summary>
    /// A judgment, not a Square limit: comfortably above an email photo (Neelam's were 1024 px
    /// wide), and small enough that an upload never strains the app.
    /// </summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    // Square's addresses run to a couple of hundred characters; this keeps the metadata small.
    private const int MaxAddressLength = 2048;

    public async Task<LibraryImage> AddAsync(
        string name, BinaryData content, string contentType, string? altText = null, CancellationToken ct = default)
    {
        name = RequireName(name);
        if (!AcceptedTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"A {contentType} file cannot go in an email; use {string.Join(", ", AcceptedTypes)}.", nameof(contentType));
        if (content.ToMemory().Length > MaxBytes)
            throw new ArgumentException($"The photo is larger than {MaxBytes / (1024 * 1024)} MB.", nameof(content));
        await RefuseTakenAsync(name, ct);

        var added = clock.GetUtcNow();
        var metadata = new Dictionary<string, string>
        {
            [NameKey] = Uri.EscapeDataString(name),
            [AddedKey] = SaveStamp.Of(added),
        };
        if (!string.IsNullOrWhiteSpace(altText)) metadata[AltKey] = Uri.EscapeDataString(altText.Trim());

        var blobName = await CreateAsync(name, content, contentType, metadata, ct);
        return new LibraryImage(name, contentType, altText?.Trim(), added, blobName);
    }

    /// <summary>
    /// Adds a photo hosted on Square, by name and address. Nothing is fetched or copied: the
    /// library keeps the name and the address, and previews show the image from Square.
    /// </summary>
    /// <exception cref="ArgumentException">No name, or an address that is not on one of <see cref="SquareHosts"/>.</exception>
    public async Task<LibraryImage> AddFromSquareAsync(string name, string address, CancellationToken ct = default)
    {
        name = RequireName(name);
        var url = SquareAddress(address);
        await RefuseTakenAsync(name, ct);

        var added = clock.GetUtcNow();
        var metadata = new Dictionary<string, string>
        {
            [NameKey] = Uri.EscapeDataString(name),
            [AddedKey] = SaveStamp.Of(added),
            [SquareKey] = Uri.EscapeDataString(url.AbsoluteUri),
        };
        // No bytes: the reference is the metadata.
        var blobName = await CreateAsync(name, new BinaryData(Array.Empty<byte>()), "application/octet-stream", metadata, ct);
        return new LibraryImage(name, "", null, added, blobName, url);
    }

    /// <summary>
    /// The address as given, query string included, when it is an https address on one of
    /// <see cref="SquareHosts"/>; otherwise says what is wrong with it.
    /// </summary>
    /// <exception cref="ArgumentException">Not an address on one of Square's image hosts.</exception>
    public static Uri SquareAddress(string? address)
    {
        // Messages without a parameter name: the page shows them to the person as they are.
        address = address?.Trim();
        if (string.IsNullOrEmpty(address))
            throw new ArgumentException("Paste the image's address from Square.");
        if (address.Length > MaxAddressLength)
            throw new ArgumentException($"That address is longer than {MaxAddressLength} characters.");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("That is not a web address. Copy the image's address from Square; it starts https://.");
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A Square image address starts https://.");
        if (!SquareHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase) || !url.IsDefaultPort || url.UserInfo.Length > 0)
            throw new ArgumentException(
                $"{url.Host} is not one of Square's image hosts, so the library cannot use it. " +
                $"Copy the address of an image in your Square library; it is on {string.Join(", ", SquareHosts)}.");
        return url;
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
            Uri? square = null;
            if (blob.Metadata.TryGetValue(SquareKey, out var s))
            {
                // Checked again on the way out, so a preview only ever loads from Square.
                try { square = SquareAddress(Uri.UnescapeDataString(s)); }
                catch (ArgumentException) { continue; }
            }
            images.Add(new LibraryImage(Uri.UnescapeDataString(name), "", alt, added, blob.Name, square));
        }
        return images.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The photo a block names, from a list of the library's photos: by name, ignoring case and the
    /// spaces around it, as names are unique. Null when the library has no photo by that name.
    /// </summary>
    public static LibraryImage? Find(IEnumerable<LibraryImage> images, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : images.FirstOrDefault(i => string.Equals(i.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An uploaded photo's bytes and type, for the editor to show and the export to hand over. A
    /// photo on Square has none here: show it from its <see cref="LibraryImage.SquareUrl"/>.
    /// </summary>
    public Task<Janet.Azure.Storage.BlobContent> ReadAsync(string name, CancellationToken ct = default) =>
        clientContainer.ReadAsync(BlobName(name.Trim()), ct);

    /// <summary>
    /// Permanently deletes a photo. Blocks that named it will no longer find it. For a photo on
    /// Square, only the reference here goes; the image at Square is not touched.
    /// </summary>
    public Task<bool> DeleteAsync(string name, CancellationToken ct = default) =>
        clientContainer.DeleteAsync(BlobName(name.Trim()), ct);

    private static string RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A photo needs a name, so blocks can refer to it.");
        return name.Trim();
    }

    private async Task RefuseTakenAsync(string name, CancellationToken ct)
    {
        if (Find(await ListAsync(ct), name) is not null)
            throw new InvalidOperationException($"The library already has a photo called \"{name}\".");
    }

    private async Task<string> CreateAsync(
        string name, BinaryData content, string contentType, Dictionary<string, string> metadata, CancellationToken ct)
    {
        var blobName = BlobName(name);
        if (!await clientContainer.TryCreateAsync(blobName, content, contentType, metadata, ct))
            throw new InvalidOperationException($"The library already has a photo called \"{name}\".");
        return blobName;
    }

    // The name, escaped: reversible, and a "/" in a name can never make a path.
    private static string BlobName(string name) => Prefix + Uri.EscapeDataString(name);
}
