using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>The client's photos: stored as bytes with their type, chosen from by name.</summary>
public class ImageLibraryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    // A real PNG signature and a few bytes: enough to prove bytes come back exactly as they went in.
    private static readonly BinaryData Png = new(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0x10 });

    private readonly InMemoryBlobBackend _container = new();
    private ImageLibrary Library => new(_container, new ManualClock(Start));

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
