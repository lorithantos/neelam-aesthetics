using System.Diagnostics;
using Azure.Core;

namespace Neelam.Web;

/// <summary>
/// Storage's first token, asked for the moment the app's identity exists, before the host is built,
/// so the slowest part of a cold start overlaps the host's own start-up instead of following it.
/// </summary>
/// <remarks>
/// <para>
/// Why (2026-10-11, both sites' logs): in a new container the first managed-identity token request
/// takes 4.5-6 s even with the plan idle, and 10.9-17.6 s with the core busy, while the next takes
/// 0.3-1 s; token requests are answered one at a time, so every caller waits behind the first. The
/// warm-up's first read used to make that request only once the host had been built and its hosted
/// services started, and with <c>WEBSITE_WARMUP_PATH</c> holding all traffic until the warm-up is done,
/// that wait was a visitor's after every restart.
/// </para>
/// <para>
/// It asks the app's own credential for exactly what the storage clients' pipelines ask for
/// (<see cref="StorageRequests"/>), so the credential's own token cache answers their first requests.
/// The tokens themselves are dropped at once: never kept, returned or logged. Only the outcome
/// ("fetched", or "failed: " and the exception's type) and how long it took are kept.
/// </para>
/// <para>
/// It can never stop or slow the start: the request runs on the thread pool, so even a credential that
/// blocks returns control at once; nothing awaits it before the server listens; its task never
/// faults. The warm-up awaits it within its own budget, and on a failure carries on as it always has.
/// In Development (the developer's <c>az login</c>, and every test host) there is none
/// (<see cref="None"/>), and the warm-up runs exactly as before.
/// </para>
/// </remarks>
public sealed class TokenPrefetch
{
    /// <summary>
    /// The scope every blob and table client of this app asks for: Azure Storage's resource ID,
    /// <c>https://storage.azure.com/</c>, which applies to any storage account (Microsoft Learn,
    /// "Authorize access to blobs by using Microsoft Entra ID", and "Authorize with Microsoft Entra ID"
    /// in the Storage REST reference), with <c>/.default</c>. It is what the clients use when no
    /// audience is set (<c>BlobClientOptions.Audience</c>: "If null, BlobAudience.DefaultAudience will be
    /// assumed", "https://storage.azure.com"), and Janet.Azure.Storage sets none.
    /// <c>TokenPrefetchTests</c> pins it against the requests the app's real clients make.
    /// </summary>
    public const string StorageScope = "https://storage.azure.com/.default";

    /// <summary>
    /// What the storage clients' pipelines ask the credential for, CAE flag included, so the same cache
    /// entries answer them: first the table clients' request (the warm-up's first read is the clients
    /// table), then the blob clients'. The two SDKs differ only in the flag (Azure.Data.Tables 12.13 asks
    /// without CAE, Azure.Storage.Blobs 12.30 with it, as TokenPrefetchTests shows); a credential may
    /// cache the two apart, so both are asked for, the second costing one quick request.
    /// </summary>
    public static IReadOnlyList<TokenRequestContext> StorageRequests() =>
    [
        new([StorageScope], isCaeEnabled: false),
        new([StorageScope], isCaeEnabled: true),
    ];

    /// <summary>No pre-fetch: off Azure, where the warm-up asks for its token as it always has.</summary>
    public static TokenPrefetch None { get; } = new(null);

    private TokenPrefetch(Task<TokenPrefetchOutcome>? outcome) => Outcome = outcome;

    /// <summary>Completes once the request has finished, however it finished; never faults. Null when there is no pre-fetch.</summary>
    public Task<TokenPrefetchOutcome>? Outcome { get; }

    /// <summary>
    /// Asks <paramref name="credential"/> for storage's token on the thread pool, and returns at once,
    /// without waiting for the answer or even for the credential to be called.
    /// </summary>
    public static TokenPrefetch Start(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new(Task.Run(() => FetchAsync(credential)));
    }

    /// <summary>
    /// Logs the outcome and its time once the request has finished ("Token pre-fetch: fetched in N ms."),
    /// a failure as a warning; never the token or a message. Nothing when there is no pre-fetch.
    /// </summary>
    public void Report(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        Outcome?.ContinueWith(done =>
        {
            var outcome = done.Result;
            if (outcome.Ok) log.LogInformation("Token pre-fetch: {Outcome} in {Ms} ms.", outcome.Outcome, outcome.Ms);
            else log.LogWarning("Token pre-fetch: {Outcome} in {Ms} ms.", outcome.Outcome, outcome.Ms);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static async Task<TokenPrefetchOutcome> FetchAsync(TokenCredential credential)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            // The tokens are dropped here: only that they came, and when, is kept. Both are asked for at
            // once; a credential that answers one at a time queues the second behind the first.
            await Task.WhenAll(StorageRequests().Select(async request =>
                _ = await credential.GetTokenAsync(request, CancellationToken.None).ConfigureAwait(false))).ConfigureAwait(false);
            return new(TokenPrefetchOutcome.Fetched, watch.ElapsedMilliseconds, Ok: true);
        }
        catch (Exception ex)
        {
            // The type only: a credential's message can quote an endpoint's answer.
            return new("failed: " + ex.GetType().Name, watch.ElapsedMilliseconds, Ok: false);
        }
    }
}

/// <summary>How the token pre-fetch ended: "fetched" or "failed: " and the exception's type, and how long it took. Never the token.</summary>
public sealed record TokenPrefetchOutcome(string Outcome, long Ms, bool Ok)
{
    public const string Fetched = "fetched";
}
