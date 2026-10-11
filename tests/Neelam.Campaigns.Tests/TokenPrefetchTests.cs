using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Janet.Azure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Storage's first token, asked for as the app starts (2026-10-11: a new container's first managed-
/// identity request takes 4.5-6 s and more, and every caller waits behind it): it asks for exactly what
/// the storage clients ask for, starts before the host is built, never stops or slows the start, is
/// awaited by the warm-up within its budget, never logs the token, and is not there off Azure. Every
/// credential here is a fake: nothing reaches the network.
/// </summary>
public class TokenPrefetchTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private const string Secret = "eyJ0eXAi.secret-token-value.never-logged";

    // ---- What it asks for ----

    // The app's real table and blob clients, built as Program builds them, ask the credential for exactly
    // these: same scope, same CAE flag (the two services' SDKs differ there), no claims or tenant.
    // Otherwise the pre-fetch would fill a cache entry nobody reads. The credential refuses before any
    // request is sent, so nothing leaves.
    [Fact]
    public async Task It_asks_for_exactly_what_the_storage_clients_ask_for()
    {
        var credential = new RefusingCredential();
        var clients = new StorageClients(
            new StorageEndpoints(Blob: new Uri("https://test.blob.core.windows.net/"), Table: new Uri("https://test.table.core.windows.net/")),
            credential);

        await Assert.ThrowsAnyAsync<Exception>(async () => { await foreach (var _ in clients.Tables.QueryAsync()) break; });
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in clients.Tables.GetTableClient("clients").QueryAsync<Azure.Data.Tables.TableEntity>()) break;
        });
        var tables = Drain(credential);
        await Assert.ThrowsAnyAsync<Exception>(async () => { await foreach (var _ in clients.Blobs.GetBlobContainersAsync()) break; });
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in clients.Blobs.GetBlobContainerClient("settings").GetBlobsAsync()) break;
        });
        var blobs = Drain(credential);

        Assert.True(tables.Length >= 2 && blobs.Length >= 2, $"The table clients asked {tables.Length} times, the blob clients {blobs.Length}.");
        var requests = TokenPrefetch.StorageRequests();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal(["https://storage.azure.com/.default"], r.Scopes));
        Assert.All(tables, asked => AssertSame(requests[0], asked));
        Assert.All(blobs, asked => AssertSame(requests[1], asked));

        static TokenRequestContext[] Drain(RefusingCredential credential)
        {
            var asked = new List<TokenRequestContext>();
            while (credential.Asked.TryDequeue(out var context)) asked.Add(context);
            return [.. asked];
        }

        static void AssertSame(TokenRequestContext expected, TokenRequestContext asked)
        {
            Assert.Equal(expected.Scopes, asked.Scopes);
            Assert.Equal(expected.IsCaeEnabled, asked.IsCaeEnabled);
            Assert.Equal(expected.Claims, asked.Claims);
            Assert.Equal(expected.TenantId, asked.TenantId);
        }
    }

    // ---- When it starts ----

    // Program starts it right after the credential exists and before the host is built, and only where
    // the credential is the managed identity: in Development (az login, every test host) there is none.
    [Fact]
    public void Program_starts_it_before_the_host_is_built_and_only_off_development()
    {
        var program = File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web", "Program.cs"));
        var credential = program.IndexOf("new ManagedIdentityCredential(", StringComparison.Ordinal);
        var start = program.IndexOf("TokenPrefetch.Start(credential)", StringComparison.Ordinal);
        var build = program.IndexOf("builder.Build()", StringComparison.Ordinal);

        Assert.True(credential >= 0 && start >= 0 && build >= 0, "Program creates the credential, starts the pre-fetch and builds the host.");
        Assert.True(credential < start && start < build, "The pre-fetch starts after the credential is created and before the host is built.");
        Assert.True(
            Regex.IsMatch(program, @"var prefetch = builder\.Environment\.IsDevelopment\(\)\s*\?\s*TokenPrefetch\.None\s*:\s*TokenPrefetch\.Start\(credential\)"),
            "Program starts the pre-fetch only off Development, where the credential is the managed identity.");
        // The first thing between the credential and the storage settings: nothing configured comes before it.
        var between = program[credential..start];
        Assert.DoesNotContain("builder.Services", between, StringComparison.Ordinal);
    }

    // The test hosts run in Development, as a developer's own run does: no pre-fetch, and the warm-up
    // runs as it always has, its first step the clients table.
    [Fact]
    public async Task Off_azure_there_is_none_and_the_warm_up_is_unchanged()
    {
        using (var demo = new DemoApp())
            Assert.Same(TokenPrefetch.None, demo.Services.GetRequiredService<TokenPrefetch>());
        Assert.Null(TokenPrefetch.None.Outcome);

        await using var app = new WarmupApp();
        await WarmupApp.SeedAsync(app);
        var state = app.Services.GetRequiredService<WarmupState>();
        await state.Completion.WaitAsync(Patience);

        Assert.DoesNotContain(state.Steps, s => s.Name == "storage token");
        Assert.Equal("clients table", state.Steps[0].Name);
    }

    // Start hands the request to the thread pool and returns at once, even when the credential blocks
    // the thread that calls it, so the start goes on; the credential is asked without anyone awaiting.
    [Fact]
    public async Task Starting_it_returns_at_once_even_when_the_credential_blocks()
    {
        var credential = new BlockingCredential();
        try
        {
            var watch = Stopwatch.StartNew();
            var prefetch = await Task.Run(() => TokenPrefetch.Start(credential)).WaitAsync(Patience);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Start took {watch.Elapsed}.");

            await credential.Called.Task.WaitAsync(Patience);
            Assert.False(prefetch.Outcome!.IsCompleted);
        }
        finally
        {
            credential.Release();
        }
    }

    // ---- What it keeps and logs ----

    [Fact]
    public async Task A_fetched_token_is_logged_by_outcome_and_time_and_never_kept()
    {
        var prefetch = TokenPrefetch.Start(new AnsweringCredential());
        var outcome = await prefetch.Outcome!.WaitAsync(Patience);

        Assert.Equal(new TokenPrefetchOutcome("fetched", outcome.Ms, Ok: true), outcome);
        Assert.DoesNotContain(Secret, outcome.ToString(), StringComparison.Ordinal);

        var log = new ListLogger<TokenPrefetch>();
        prefetch.Report(log);
        var entry = await Logged(log);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Matches(new Regex(@"^Token pre-fetch: fetched in \d+ ms\.$"), entry.Message);
        Assert.Null(entry.Error);
    }

    // A credential that throws, at once or later, ends the pre-fetch as "failed: " and the exception's
    // type: the task never faults, and the log never carries the exception or its message.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failure_is_its_outcome_never_a_fault_and_never_its_message(bool synchronously)
    {
        var prefetch = TokenPrefetch.Start(new ThrowingCredential(synchronously));
        var outcome = await prefetch.Outcome!.WaitAsync(Patience);

        Assert.False(prefetch.Outcome.IsFaulted);
        Assert.Equal("failed: AuthenticationFailedException", outcome.Outcome);
        Assert.False(outcome.Ok);

        var log = new ListLogger<TokenPrefetch>();
        prefetch.Report(log);
        var entry = await Logged(log);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Matches(new Regex(@"^Token pre-fetch: failed: AuthenticationFailedException in \d+ ms\.$"), entry.Message);
        Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Error);
    }

    [Fact]
    public void With_none_nothing_is_logged()
    {
        var log = new ListLogger<TokenPrefetch>();
        TokenPrefetch.None.Report(log);
        Assert.Empty(log.Entries);
    }

    // ---- The warm-up awaits it ----

    // Its first step is the pre-fetch's answer; the clients table is read only after it, so its token
    // comes from the cache rather than a second request queued behind the first.
    [Fact]
    public async Task The_warm_up_awaits_it_before_reading_storage()
    {
        var credential = new GatedCredential();
        await using var app = new WarmupApp { Prefetch = TokenPrefetch.Start(credential) };
        await WarmupApp.SeedAsync(app);
        var state = app.Services.GetRequiredService<WarmupState>();
        await credential.Called.Task.WaitAsync(Patience);

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Empty(state.Steps);
        Assert.False(state.IsDone);

        credential.Answer();
        await state.Completion.WaitAsync(Patience);
        var steps = state.Steps;
        Assert.Equal(new WarmStep("storage token", "fetched", steps[0].Ms, Ok: true), steps[0]);
        Assert.Equal("clients table", steps[1].Name);
        Assert.Contains(steps, s => s.Name == "GET /campaigns" && s.Outcome == "200");
        Assert.Contains(app.Log.Entries, e => Regex.IsMatch(e.Message, @"^Warm-up storage token: fetched in \d+ ms\.$"));
        Assert.All(app.Log.Entries, e => Assert.DoesNotContain(Secret, e.Message, StringComparison.Ordinal));
    }

    // A failed pre-fetch is the step's outcome; the warm-up carries on as before there was one.
    [Fact]
    public async Task After_a_failed_pre_fetch_the_warm_up_carries_on()
    {
        await using var app = new WarmupApp { Prefetch = TokenPrefetch.Start(new ThrowingCredential(synchronously: true)) };
        await WarmupApp.SeedAsync(app);
        var state = app.Services.GetRequiredService<WarmupState>();
        await state.Completion.WaitAsync(Patience);

        var steps = state.Steps;
        Assert.Equal(new WarmStep("storage token", "failed: AuthenticationFailedException", steps[0].Ms, Ok: false), steps[0]);
        Assert.Equal(new WarmStep("clients table", "read", steps[1].Ms, Ok: true), steps[1]);
        Assert.Contains(steps, s => s.Name == "GET /campaigns" && s.Outcome == "200");
        Assert.Contains(app.Log.Entries, e => e.Level == LogLevel.Warning
            && Regex.IsMatch(e.Message, @"^Warm-up storage token: failed: AuthenticationFailedException in \d+ ms\.$"));
        Assert.Contains(app.Log.Entries, e => e.Message.StartsWith("Warm-up done in", StringComparison.Ordinal));
        Assert.All(app.Log.Entries, e => Assert.DoesNotContain(Secret, e.Message, StringComparison.Ordinal));
    }

    // A pre-fetch that never answers, from a credential that blocks its thread outright: the host
    // starts and answers, the warm-up gives up at its budget, and then the gate is open.
    [Fact]
    public async Task A_hanging_pre_fetch_holds_nothing_past_the_budget()
    {
        var credential = new BlockingCredential();
        try
        {
            var budget = TimeSpan.FromSeconds(1);
            var prefetch = await Task.Run(() => TokenPrefetch.Start(credential)).WaitAsync(Patience);
            await using var app = new WarmupApp { Prefetch = prefetch, Budget = budget, GateWait = TimeSpan.FromMilliseconds(100) };
            await WarmupApp.SeedAsync(app);
            var watch = Stopwatch.StartNew();

            var client = await Task.Run(() => app.CreateClient(new() { AllowAutoRedirect = false })).WaitAsync(Patience);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz").WaitAsync(Patience)).StatusCode);
            await credential.Called.Task.WaitAsync(Patience);

            var state = app.Services.GetRequiredService<WarmupState>();
            await state.Completion.WaitAsync(Patience);
            Assert.True(watch.Elapsed < budget + TimeSpan.FromSeconds(10), $"The warm-up finished after {watch.Elapsed}.");
            Assert.False(app.Services.GetRequiredService<TokenPrefetch>().Outcome!.IsCompleted);
            Assert.DoesNotContain(state.Steps, s => s.Name == "storage token");
            Assert.Contains(app.Log.Entries, e => e.Message.StartsWith("Warm-up gave up after", StringComparison.Ordinal));

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/how-it-works").WaitAsync(Patience)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(WarmupEndpoint.Path).WaitAsync(Patience)).StatusCode);
        }
        finally
        {
            credential.Release();
        }
    }

    private static async Task<(LogLevel Level, string Message, Exception? Error)> Logged(ListLogger<TokenPrefetch> log)
    {
        var watch = Stopwatch.StartNew();
        while (log.Entries.Count == 0 && watch.Elapsed < Patience) await Task.Delay(10);
        return Assert.Single(log.Entries);
    }

    private static AccessToken Token() => new(Secret, DateTimeOffset.UtcNow.AddHours(1));

    /// <summary>Notes what it was asked for, then refuses, so the client's request is never sent.</summary>
    private sealed class RefusingCredential : TokenCredential
    {
        public System.Collections.Concurrent.ConcurrentQueue<TokenRequestContext> Asked { get; } = new();

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Asked.Enqueue(requestContext);
            throw new CredentialUnavailableException("Refused by the test.");
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class AnsweringCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => Token();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Token());
    }

    /// <summary>Answers once the test says so.</summary>
    private sealed class GatedCredential : TokenCredential
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Answer() => _gate.TrySetResult();

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Called.TrySetResult();
            await _gate.Task;
            return Token();
        }
    }

    /// <summary>Throws, at once or after a yield, with a message quoting what looks like a token.</summary>
    private sealed class ThrowingCredential(bool synchronously) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new AuthenticationFailedException("The endpoint answered " + Secret);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            synchronously ? throw new AuthenticationFailedException("The endpoint answered " + Secret) : Later();

        private static async ValueTask<AccessToken> Later()
        {
            await Task.Yield();
            throw new AuthenticationFailedException("The endpoint answered " + Secret);
        }
    }

    /// <summary>Blocks the calling thread until released: the worst a credential can do to its caller.</summary>
    // Not disposed: the blocked thread may still be waking from Release when a test ends.
    private sealed class BlockingCredential : TokenCredential
    {
        private readonly ManualResetEventSlim _release = new();

        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.Set();

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Called.TrySetResult();
            _release.Wait(CancellationToken.None);
            return Token();
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
