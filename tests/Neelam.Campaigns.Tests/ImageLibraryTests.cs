using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>The client's photos: stored as bytes with their type, chosen from by name.</summary>
public class ImageLibraryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    // A real PNG signature and a few bytes: enough to prove bytes come back exactly as they went in.
    private static readonly BinaryData Png = new(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0x10 });

    private readonly InMemoryBlobBackend _container = new();
    private TestRecords? _records;
    private TestRecords Records => _records ??= new(new ManualClock(Start));
    private ImageLibrary Library => Records.Images(_container);

    [Fact]
    public async Task A_photo_comes_back_byte_for_byte_with_its_type()
    {
        await Library.AddAsync("Principals seated", Png, "image/png", "The clinic's three principals");

        var stored = await Library.ReadAsync("Principals seated");

        Assert.Equal(Png.ToArray(), stored.Content.ToArray());
        Assert.Equal("image/png", stored.ContentType);
    }

    [Fact]
    public async Task The_library_lists_photos_by_name_with_their_alt_text()
    {
        await Library.AddAsync("Principals toasting", Png, "image/png");
        await Library.AddAsync("Principals seated", Png, "image/jpeg", "Seated, with glasses raised");

        var images = await Library.ListAsync();

        Assert.Equal(["Principals seated", "Principals toasting"], images.Select(i => i.Name));
        Assert.Equal("Seated, with glasses raised", images[0].AltText);
        Assert.Null(images[1].AltText);
        Assert.All(images, i => Assert.Equal(Start, i.AddedAt));
    }

    [Fact]
    public async Task Two_photos_cannot_share_a_name()
    {
        await Library.AddAsync("Principals seated", Png, "image/png");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Library.AddAsync("principals SEATED", Png, "image/png"));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    public async Task Only_formats_an_email_shows_are_accepted(string type) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Library.AddAsync("File", Png, type));

    [Fact]
    public async Task A_photo_over_the_size_limit_is_refused() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Library.AddAsync("Huge", new BinaryData(new byte[ImageLibrary.MaxBytes + 1]), "image/png"));

    // Names are escaped into the blob name, so a "/" can never make a path of its own.
    [Fact]
    public async Task A_slash_in_a_name_stays_inside_the_library()
    {
        var image = await Library.AddAsync("Before/after", Png, "image/png");

        Assert.Equal("images/Before%2Fafter", image.BlobName);
        Assert.Equal("Before/after", Assert.Single(await Library.ListAsync()).Name);
    }

    [Fact]
    public async Task Deleting_a_photo_leaves_nothing()
    {
        await Library.AddAsync("Principals seated", Png, "image/png");

        Assert.True(await Library.DeleteAsync("Principals seated"));

        Assert.Empty(_container.Blobs);
        Assert.Empty(await Library.ListAsync());
    }

    // Photos hosted on Square (owner, 2026-10-09): a name and Square's address, never the image.

    private const string Toasting =
        "https://postoffice-production-f.squarecdn.com/images/principals-toasting.jpg?enable=upscale&height=196&width=640";

    [Theory]
    [InlineData("postoffice-production-f.squarecdn.com")]
    [InlineData("square-web-production-f.squarecdn.com")]
    [InlineData("square-postoffice-production.s3.amazonaws.com")]
    public async Task Each_of_Square_s_image_hosts_is_accepted(string host)
    {
        var image = await Library.AddFromSquareAsync("Principals toasting", $"https://{host}/photo.jpg");

        Assert.Equal(host, image.SquareUrl!.Host);
        Assert.Equal(host, Assert.Single(await Library.ListAsync()).SquareUrl!.Host);
    }

    [Theory]
    [InlineData("https://example.com/photo.jpg", "example.com is not one of Square's image hosts")]
    [InlineData("https://postoffice-production-f.squarecdn.com.example.com/photo.jpg", "is not one of Square's image hosts")]
    [InlineData("https://squarecdn.com/photo.jpg", "squarecdn.com is not one of Square's image hosts")]
    [InlineData("http://postoffice-production-f.squarecdn.com/photo.jpg", "starts https://")]
    [InlineData("postoffice-production-f.squarecdn.com/photo.jpg", "not a web address")]
    public async Task Any_other_address_is_refused_with_a_clear_message(string address, string says)
    {
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => Library.AddFromSquareAsync("Principals toasting", address));

        Assert.Contains(says, refused.Message);
        Assert.Empty(_container.Blobs);
    }

    // Addresses that name a Square host but would not load from it as given: credentials before the
    // host, or another port on it.
    [Theory]
    [InlineData("https://x@postoffice-production-f.squarecdn.com/photo.jpg")]
    [InlineData("https://postoffice-production-f.squarecdn.com:8443/photo.jpg")]
    [InlineData("https://square-postoffice-production.s3.amazonaws.com:444/photo.jpg")]
    public async Task A_Square_host_with_userinfo_or_another_port_is_refused(string address)
    {
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => Library.AddFromSquareAsync("Principals toasting", address));

        Assert.Contains("is not one of Square's image hosts", refused.Message);
        Assert.Empty(_container.Blobs);
    }

    // A host is a host whatever its case; the address is kept as the web reads it, host in lower case.
    [Fact]
    public async Task A_Square_host_in_capitals_is_accepted()
    {
        var image = await Library.AddFromSquareAsync("Principals toasting", "https://POSTOFFICE-Production-F.SquareCDN.com/photo.jpg");

        Assert.Equal("https://postoffice-production-f.squarecdn.com/photo.jpg", image.SquareUrl!.AbsoluteUri);
        Assert.Equal(image.SquareUrl, Assert.Single(await Library.ListAsync()).SquareUrl);
    }

    // An entry whose stored address was changed outside the library (or written before a host was
    // dropped) is checked again on the way out. It is listed, with no address, so it can be seen and
    // deleted but never shown, and its name stays taken rather than vanishing.
    [Theory]
    [InlineData("https://example.com/photo.jpg")]
    [InlineData("https://x@postoffice-production-f.squarecdn.com/photo.jpg")]
    [InlineData("https://postoffice-production-f.squarecdn.com:8443/photo.jpg")]
    [InlineData("http://postoffice-production-f.squarecdn.com/photo.jpg")]
    public async Task A_tampered_stored_entry_is_listed_as_not_on_Square_and_can_be_deleted(string stored)
    {
        await Library.AddFromSquareAsync("Principals seated", "https://square-web-production-f.squarecdn.com/seated.jpg");
        _container.Put("images/Principals%20toasting", "", new Dictionary<string, string>
        {
            ["name"] = "Principals%20toasting", ["added"] = "20261003T090000.0000000Z",
            ["square"] = Uri.EscapeDataString(stored), ["entry"] = Guid.NewGuid().ToString("N"),
        });

        var images = await Library.ListAsync();

        Assert.Equal(["Principals seated", "Principals toasting"], images.Select(i => i.Name));
        var tampered = images[1];
        Assert.True(tampered.NotASquareAddress);
        Assert.Null(tampered.SquareUrl);
        Assert.False(tampered.IsOnSquare);
        Assert.False(images[0].NotASquareAddress);
        // Its name is still taken, and says so truthfully.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Library.AddFromSquareAsync("Principals toasting", "https://postoffice-production-f.squarecdn.com/toasting.jpg"));

        Assert.True(await Library.DeleteAsync("Principals toasting"));
        Assert.Equal("Principals seated", Assert.Single(await Library.ListAsync()).Name);
    }

    // Square crops and sizes an image through its query string, so the address is kept whole.
    [Theory]
    [InlineData(Toasting)]
    [InlineData("https://square-web-production-f.squarecdn.com/files/abc123/original.jpeg?crop=1:1")]
    public async Task The_address_keeps_its_query_string(string address)
    {
        Assert.Equal(address, (await Library.AddFromSquareAsync("Principals toasting", address)).SquareUrl!.AbsoluteUri);

        Assert.Equal(address, Assert.Single(await Library.ListAsync()).SquareUrl!.AbsoluteUri);
    }

    // Kept the way an upload's name is kept, in the blob's metadata: no image bytes are stored. The
    // entry's random id is what the activity trail calls it, since its name is content.
    [Fact]
    public async Task A_Square_photo_stores_only_its_name_and_address()
    {
        await Library.AddFromSquareAsync("Principals toasting", Toasting);

        var (name, (blob, metadata)) = Assert.Single(_container.Blobs);
        Assert.Equal("images/Principals%20toasting", name);
        Assert.Equal(0, blob.Content.ToMemory().Length);
        Assert.Equal(["added", "entry", "name", "square"], metadata.Keys.Order());
        Assert.Equal(Toasting, Uri.UnescapeDataString(metadata["square"]));
        Assert.True(Guid.TryParseExact(metadata["entry"], "N", out _));
    }

    // A photo added before Square photos existed has no address, and reads as the upload it is.
    [Fact]
    public async Task A_photo_saved_before_Square_photos_still_reads_as_an_upload()
    {
        _container.Put("images/Principals%20seated", "", new Dictionary<string, string>
        {
            ["name"] = "Principals%20seated", ["alt"] = "Seated", ["added"] = "20261003T090000.0000000Z",
        });

        var image = Assert.Single(await Library.ListAsync());

        Assert.Equal("Principals seated", image.Name);
        Assert.Equal("Seated", image.AltText);
        Assert.Equal(Start, image.AddedAt);
        Assert.False(image.IsOnSquare);
    }

    [Fact]
    public async Task A_Square_photo_cannot_take_an_uploaded_photo_s_name()
    {
        await Library.AddAsync("Principals toasting", Png, "image/png");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Library.AddFromSquareAsync("principals TOASTING", Toasting));
    }

    // Deleting removes the reference and nothing else: the other photos stay, and nothing here
    // could reach Square.
    [Fact]
    public async Task Add_list_then_delete_removes_only_that_entry()
    {
        await Library.AddFromSquareAsync("Principals toasting", Toasting);
        await Library.AddAsync("Principals seated", Png, "image/png");
        Assert.Equal(["Principals seated", "Principals toasting"], (await Library.ListAsync()).Select(i => i.Name));

        Assert.True(await Library.DeleteAsync("Principals toasting"));

        Assert.Equal("Principals seated", Assert.Single(await Library.ListAsync()).Name);
        Assert.Equal(["images/Principals%20seated"], _container.Blobs.Keys);
        Assert.Equal(Png.ToArray(), (await Library.ReadAsync("Principals seated")).Content.ToArray());
    }

    [Fact]
    public void A_block_s_photo_is_found_by_name_ignoring_case_and_spaces()
    {
        var images = new[] { new LibraryImage("Principals toasting", "", null, Start, "images/x", new Uri(Toasting)) };

        Assert.Same(images[0], ImageLibrary.Find(images, "  principals TOASTING "));
        Assert.Null(ImageLibrary.Find(images, "Principals seated"));
        Assert.Null(ImageLibrary.Find(images, " "));
    }

    // The library shares the client's container with saves and documents, and never mixes with them.
    [Fact]
    public async Task Photos_and_documents_keep_to_their_own_prefixes()
    {
        await Library.AddAsync("Principals seated", Png, "image/png");
        await ClientStores.CatalogIn(_container, new ManualClock(Start)).SaveAsync(Neelam.Campaigns.ClientCatalog.Empty);

        Assert.Single(await Library.ListAsync());
        Assert.Single(await ClientStores.CatalogIn(_container, new ManualClock(Start)).HistoryAsync());
    }
}
