using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace Neelam.Campaigns.Claude;

/// <summary>A photo's bytes as fetched, or why it was not.</summary>
/// <param name="Refusal">"Couldn't check this photo: ..." when it was not fetched; null when it was.</param>
public sealed record FetchedPhoto(byte[]? Bytes, string? MediaType, string? Refusal)
{
    public static FetchedPhoto Refused(string why) => new(null, null, $"Couldn't check this photo: {why}.");
}

/// <summary>
/// Fetches a photo of her library from Square, for the AI proofread to hash and read (owner,
/// 2026-10-10: "we should take extreme care hitting random web sites. We should both parse them and
/// allowlist the domains"). The app fetches the bytes itself and never hands an address to anyone
/// else to fetch. Every address, the first and each redirect, is parsed and held to the allowlist:
/// absolute https, no user or password, the default port, a host name (never an IP address) that,
/// lower-cased and IDN-normalised, is exactly one of the allowed hosts
/// (<c>ImageLibrary.SquareHosts</c>, the one list). Redirects are not followed by the client: one to
/// another allowed address is followed here, at most <see cref="MaxRedirects"/> times. The body is
/// read to at most <see cref="MaxBytes"/>, the whole fetch within <see cref="Timeout"/>, and kept only
/// when its Content-Type is a photo format email takes and its first bytes are that format's. Any
/// refusal comes back as words for her, never as an exception.
/// </summary>
public sealed class SquarePhotoFetcher
{
    /// <summary>The name of the app's one HttpClient for this, configured by <see cref="Handler"/>.</summary>
    public const string HttpClientName = "square-photos";

    public const int MaxRedirects = 2;

    public const long MaxBytes = 10 * 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The formats email clients show, as the image library accepts them.</summary>
    public static readonly IReadOnlyList<string> MediaTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"];

    private readonly HttpClient _http;
    private readonly HashSet<string> _hosts;
    private readonly TimeSpan _timeout;

    /// <param name="http">A client over <see cref="Handler"/> (or a test's handler).</param>
    /// <param name="hosts">The allowed hosts: <c>ImageLibrary.SquareHosts</c>.</param>
    public SquarePhotoFetcher(HttpClient http, IReadOnlyCollection<string> hosts) : this(http, hosts, Timeout)
    {
    }

    // A shorter limit, so a test of it need not wait ten seconds.
    internal SquarePhotoFetcher(HttpClient http, IReadOnlyCollection<string> hosts, TimeSpan timeout)
    {
        _http = http;
        _hosts = hosts.Select(h => h.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        _timeout = timeout;
    }

    /// <summary>
    /// The handler behind the app's client: no redirects followed, no cookies, no credentials, no
    /// proxy, no decompression surprises, and a connect timeout inside the fetch's own.
    /// </summary>
    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxResponseHeadersLength = 64,
    };

    /// <summary>
    /// Why <paramref name="address"/> may not be fetched, or null when it may: parsed again from its
    /// text, then held to every rule. The same check is made on each redirect.
    /// </summary>
    public string? Refusal(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri))
            return "not a web address";
        if (uri.Scheme != Uri.UriSchemeHttps) return "not a secure (https) address";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "the address carries a user name or password";
        if (!uri.IsDefaultPort) return "the address names a port";
        if (uri.HostNameType != UriHostNameType.Dns) return "the address is not a host name";
        string host;
        try
        {
            host = new IdnMapping().GetAscii(uri.IdnHost).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return "not a Square address";
        }
        return _hosts.Contains(host) ? null : "not a Square address";
    }

    /// <summary>The photo at <paramref name="address"/>, or why not; never throws but for her own cancellation.</summary>
    public async Task<FetchedPhoto> FetchAsync(string address, CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout);
        try
        {
            return await FetchWithinAsync(address, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FetchedPhoto.Refused("Square did not answer in time");
        }
        catch (HttpRequestException)
        {
            return FetchedPhoto.Refused("Square could not be reached");
        }
        catch (IOException)
        {
            return FetchedPhoto.Refused("the photo stopped part-way");
        }
    }

    private async Task<FetchedPhoto> FetchWithinAsync(string address, CancellationToken ct)
    {
        var current = address;
        for (var hop = 0; ; hop++)
        {
            if (Refusal(current) is { } why) return FetchedPhoto.Refused(hop == 0 ? why : $"it redirects to an address that is {why}");
            var uri = new Uri(current.Trim(), UriKind.Absolute);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("image/*");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (hop >= MaxRedirects) return FetchedPhoto.Refused("it redirects too many times");
                if (response.Headers.Location is not { } location) return FetchedPhoto.Refused("it redirects nowhere");
                current = (location.IsAbsoluteUri ? location : new Uri(uri, location)).OriginalString;
                continue;
            }
            if (!response.IsSuccessStatusCode)
                return FetchedPhoto.Refused($"Square answered {(int)response.StatusCode}");

            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (mediaType is null || !MediaTypes.Contains(mediaType))
                return FetchedPhoto.Refused("Square did not send a photo");
            if (response.Content.Headers.ContentLength > MaxBytes)
                return FetchedPhoto.Refused($"it is larger than {MaxBytes / (1024 * 1024)} MB");

            var bytes = await ReadAtMostAsync(response.Content, ct);
            if (bytes is null) return FetchedPhoto.Refused($"it is larger than {MaxBytes / (1024 * 1024)} MB");
            if (!StartsAs(bytes, mediaType)) return FetchedPhoto.Refused("its bytes are not the photo format Square named");
            return new FetchedPhoto(bytes, mediaType, null);
        }
    }

    // The body, or null when it runs past MaxBytes: reading stops there.
    private static async Task<byte[]?> ReadAtMostAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Whether the bytes begin as <paramref name="mediaType"/>'s files do.</summary>
    public static bool StartsAs(ReadOnlySpan<byte> bytes, string mediaType) => mediaType switch
    {
        "image/jpeg" => bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
        "image/png" => bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
        "image/webp" => bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };
}
