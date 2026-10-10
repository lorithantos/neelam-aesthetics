using System.Net;
using System.Net.Http.Headers;
using Neelam.Campaigns.Claude;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The one way the app fetches anything for the proofread (owner, 2026-10-10: "we should take extreme
/// care hitting random web sites. We should both parse them and allowlist the domains"). No network:
/// a fake handler answers, and counts what was asked of it.
/// </summary>
public class SquarePhotoFetcherTests
{
    internal const string Square = "https://postoffice-production-f.squarecdn.com/photo.jpg?width=640";

    internal static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9];

    /// <summary>Answers each request with what the test says, and keeps every address asked for.</summary>
    internal sealed class FakeSquare(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public FakeSquare(Func<HttpRequestMessage, HttpResponseMessage> answer) : this((r, _) => Task.FromResult(answer(r)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            Requests.Add(request);
            return answer(request, cancellationToken);
        }

        public static HttpResponseMessage Photo(byte[] bytes, string type = "image/jpeg")
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(type);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        public static HttpResponseMessage Redirect(string to) =>
            new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };
    }

    internal static SquarePhotoFetcher Fetcher(HttpMessageHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler), ImageLibrary.SquareHosts, timeout ?? SquarePhotoFetcher.Timeout);

    [Fact]
    public async Task A_square_photo_is_fetched_whole()
    {
        var square = new FakeSquare(_ => FakeSquare.Photo(Jpeg));

        var photo = await Fetcher(square).FetchAsync(Square);

        Assert.Null(photo.Refusal);
        Assert.Equal(Jpeg, photo.Bytes);
        Assert.Equal("image/jpeg", photo.MediaType);
        Assert.Equal(new Uri(Square), Assert.Single(square.Asked));
    }

    // Each refused before anything is asked of anyone.
    [Theory]
    [InlineData("http://postoffice-production-f.squarecdn.com/photo.jpg", "not a secure (https) address")]
    [InlineData("https://user:pass@postoffice-production-f.squarecdn.com/photo.jpg", "the address carries a user name or password")]
    [InlineData("https://user@postoffice-production-f.squarecdn.com/photo.jpg", "the address carries a user name or password")]
    [InlineData("https://postoffice-production-f.squarecdn.com:8443/photo.jpg", "the address names a port")]
    [InlineData("https://93.184.216.34/photo.jpg", "the address is not a host name")]
    [InlineData("https://[2606:2800:220:1:248:1893:25c8:1946]/photo.jpg", "the address is not a host name")]
    [InlineData("https://postoffice-production-f.squarecdn.com.evil.test/photo.jpg", "not a Square address")]
    [InlineData("https://evilsquarecdn.com/photo.jpg", "not a Square address")]
    [InlineData("https://squarecdn.com/photo.jpg", "not a Square address")]
    [InlineData("https://x.postoffice-production-f.squarecdn.com/photo.jpg", "not a Square address")]
    [InlineData("https://postoffice-production-f.squarecdn.com./photo.jpg", "not a Square address")]
    [InlineData("https://pоstoffice-production-f.squarecdn.com/photo.jpg", "not a Square address")] // Cyrillic о
    [InlineData("postoffice-production-f.squarecdn.com/photo.jpg", "not a web address")]
    [InlineData("", "not a web address")]
    public async Task An_address_off_the_allowlist_is_refused_and_never_asked_for(string address, string why)
    {
        var square = new FakeSquare(_ => FakeSquare.Photo(Jpeg));

        var photo = await Fetcher(square).FetchAsync(address);

        Assert.Equal($"Couldn't check this photo: {why}.", photo.Refusal);
        Assert.Null(photo.Bytes);
        Assert.Empty(square.Asked);
    }

    [Fact]
    public void A_host_in_capitals_is_the_same_host() =>
        Assert.Null(Fetcher(new FakeSquare(_ => FakeSquare.Photo(Jpeg))).Refusal("https://POSTOFFICE-production-f.SquareCDN.com/p.jpg"));

    [Fact]
    public async Task A_redirect_to_another_allowed_host_is_followed_and_checked()
    {
        var square = new FakeSquare(r => r.RequestUri!.Host == "postoffice-production-f.squarecdn.com"
            ? FakeSquare.Redirect("https://square-web-production-f.squarecdn.com/moved.jpg")
            : FakeSquare.Photo(Jpeg));

        var photo = await Fetcher(square).FetchAsync(Square);

        Assert.Null(photo.Refusal);
        Assert.Equal(["postoffice-production-f.squarecdn.com", "square-web-production-f.squarecdn.com"], square.Asked.Select(u => u.Host));
    }

    [Theory]
    [InlineData("https://evil.test/photo.jpg")]
    [InlineData("http://square-web-production-f.squarecdn.com/photo.jpg")]
    [InlineData("https://square-web-production-f.squarecdn.com:444/photo.jpg")]
    public async Task A_redirect_off_the_allowlist_is_refused_and_not_followed(string to)
    {
        var square = new FakeSquare(_ => FakeSquare.Redirect(to));

        var photo = await Fetcher(square).FetchAsync(Square);

        Assert.StartsWith("Couldn't check this photo: it redirects to an address that is", photo.Refusal);
        Assert.Single(square.Asked);
    }

    [Fact]
    public async Task Two_redirects_are_followed_and_a_third_is_refused()
    {
        var hops = 0;
        var square = new FakeSquare(_ => FakeSquare.Redirect($"https://square-web-production-f.squarecdn.com/hop{++hops}.jpg"));

        var photo = await Fetcher(square).FetchAsync(Square);

        Assert.Equal("Couldn't check this photo: it redirects too many times.", photo.Refusal);
        Assert.Equal(1 + SquarePhotoFetcher.MaxRedirects, square.Asked.Count);
    }

    [Fact]
    public async Task Two_redirects_then_the_photo_is_fine()
    {
        var square = new FakeSquare(r => r.RequestUri!.AbsolutePath switch
        {
            "/photo.jpg" => FakeSquare.Redirect("/one.jpg"),
            "/one.jpg" => FakeSquare.Redirect("https://square-web-production-f.squarecdn.com/two.jpg"),
            _ => FakeSquare.Photo(Jpeg),
        });

        var photo = await Fetcher(square).FetchAsync(Square);

        Assert.Null(photo.Refusal);
        Assert.Equal(3, square.Asked.Count);
    }

    [Fact]
    public async Task A_photo_that_says_it_is_too_big_is_refused()
    {
        var square = new FakeSquare(_ =>
        {
            var answer = FakeSquare.Photo(Jpeg);
            answer.Content.Headers.ContentLength = SquarePhotoFetcher.MaxBytes + 1;
            return answer;
        });

        Assert.Equal("Couldn't check this photo: it is larger than 10 MB.", (await Fetcher(square).FetchAsync(Square)).Refusal);
    }

    // No length given: reading stops past the limit.
    [Fact]
    public async Task A_photo_that_runs_past_the_limit_is_refused()
    {
        var big = new byte[SquarePhotoFetcher.MaxBytes + 1];
        Jpeg.CopyTo(big, 0);
        var square = new FakeSquare(_ =>
        {
            var content = new StreamContent(new MemoryStream(big));
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        Assert.Equal("Couldn't check this photo: it is larger than 10 MB.", (await Fetcher(square).FetchAsync(Square)).Refusal);
    }

    [Fact]
    public async Task Exactly_the_limit_is_fine()
    {
        var full = new byte[SquarePhotoFetcher.MaxBytes];
        Jpeg.CopyTo(full, 0);
        var square = new FakeSquare(_ => FakeSquare.Photo(full));

        Assert.Null((await Fetcher(square).FetchAsync(Square)).Refusal);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    public async Task Anything_but_a_photo_format_is_refused(string type)
    {
        var square = new FakeSquare(_ => FakeSquare.Photo(Jpeg, type));

        Assert.Equal("Couldn't check this photo: Square did not send a photo.", (await Fetcher(square).FetchAsync(Square)).Refusal);
    }

    [Fact]
    public async Task Bytes_that_are_not_the_named_format_are_refused()
    {
        var square = new FakeSquare(_ => FakeSquare.Photo(Png, "image/jpeg"));

        Assert.Equal("Couldn't check this photo: its bytes are not the photo format Square named.", (await Fetcher(square).FetchAsync(Square)).Refusal);
    }

    [Theory]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, true)]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, true)]
    [InlineData("image/gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, true)]
    [InlineData("image/gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, true)]
    [InlineData("image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, true)]
    [InlineData("image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 }, false)]
    [InlineData("image/png", new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, false)]
    [InlineData("image/gif", new byte[] { 0x47, 0x49, 0x46 }, false)]
    public void Each_format_is_known_by_its_first_bytes(string type, byte[] bytes, bool matches) =>
        Assert.Equal(matches, SquarePhotoFetcher.StartsAs(bytes, type));

    [Fact]
    public async Task An_error_from_square_is_refused()
    {
        var square = new FakeSquare(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Equal("Couldn't check this photo: Square answered 404.", (await Fetcher(square).FetchAsync(Square)).Refusal);
    }

    [Fact]
    public async Task A_square_that_does_not_answer_in_time_is_refused()
    {
        // A server that never answers: the fetch ends at its own limit.
        var waiting = new FakeSquare(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return FakeSquare.Photo(Jpeg);
        });

        var photo = await Fetcher(waiting, TimeSpan.FromMilliseconds(200)).FetchAsync(Square);

        Assert.Equal("Couldn't check this photo: Square did not answer in time.", photo.Refusal);
    }

    [Fact]
    public async Task Square_unreachable_is_refused_not_thrown()
    {
        var square = new FakeSquare(_ => throw new HttpRequestException("no route"));

        Assert.Equal("Couldn't check this photo: Square could not be reached.", (await Fetcher(square).FetchAsync(Square)).Refusal);
    }

    [Fact]
    public async Task Nothing_but_the_photo_is_asked_for_no_cookie_no_credential()
    {
        var square = new FakeSquare(_ => FakeSquare.Photo(Jpeg));

        await Fetcher(square).FetchAsync(Square);

        var request = Assert.Single(square.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Fact]
    public void The_app_s_client_follows_no_redirect_and_sends_no_cookie_credential_or_proxy()
    {
        using var handler = SquarePhotoFetcher.Handler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.False(handler.PreAuthenticate);
        Assert.False(handler.UseProxy);
    }
}
