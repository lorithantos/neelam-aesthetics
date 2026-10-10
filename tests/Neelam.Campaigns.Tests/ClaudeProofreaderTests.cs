using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Neelam.Campaigns;
using Neelam.Campaigns.Claude;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// What Claude is sent and how its answer is read. No network, ever: the SDK's HTTP goes to a fake
/// that keeps each request's JSON and answers with a canned message, and the photos come from
/// <see cref="SquarePhotoFetcherTests.FakeSquare"/>.
/// </summary>
public class ClaudeProofreaderTests
{
    internal static readonly ProofreadInstructions Instructions = ProofreadInstructions.Shipped(AppContext.BaseDirectory);

    private static readonly ProofreadRequestSettings Api = new(ClaudeProofreader.DefaultModel, Effort.Medium, 16000, ServerSideFallback: true);

    private const string HeaderPhoto = "https://postoffice-production-f.squarecdn.com/header.jpg?height=196";
    private const string OfferPhoto = "https://square-web-production-f.squarecdn.com/offer.jpg?crop=1:1";

    // Back to school, as Neelam's email had it, with its offer partly in a photo: b1 header (photo),
    // b2 heading, b3 the opening's text, b4 the offer photo, b5 the button.
    internal static Campaign BackToSchool() => new("Back to school",
    [
        new HeaderBlock("Header", "Neelam Aesthetics", new ImageRef("Principals toasting", "The principals, toasting")),
        new HeadingBlock("Heading", "BACK TO SCHOOL"),
        new ParagraphsBlock("Opening", ["Teachers get $50 off 30+ units this September.", "Book before Septembr 30."]),
        new ImageBlock("Offer photo", new ImageRef("Teacher offer")),
        new ButtonBlock("Button", new CallToAction("Book now", new Uri("https://example.com/book"))),
    ]);

    private static BusinessContext Business(IPhotoReadings? readings = null) => new("Neelam Aesthetics", "A family-run clinic.")
    {
        PhotoAddresses = new Dictionary<string, Uri>
        {
            ["Principals toasting"] = new(HeaderPhoto),
            ["Teacher offer"] = new(OfferPhoto),
        },
        PhotoReadings = readings,
    };

    /// <summary>The Anthropic API as the SDK sees it: keeps each request, answers with a canned message.</summary>
    internal sealed class FakeApi(Func<string> reply) : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(), Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>A message as the API answers one.</summary>
    internal static string Message(string text, string stopReason = "end_turn", string? stopDetails = null, int cacheRead = 0) =>
        new JsonObject
        {
            ["id"] = "msg_test",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = ClaudeProofreader.DefaultModel,
            ["content"] = text.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["stop_details"] = stopDetails is null ? null : JsonNode.Parse(stopDetails),
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 4321,
                ["output_tokens"] = 876,
                ["cache_creation_input_tokens"] = 0,
                ["cache_read_input_tokens"] = cacheRead,
            },
        }.ToJsonString();

    internal const string Clean = """{"findings":[],"photos":[]}""";

    /// <summary>Keeps what a logger is told, formatted.</summary>
    internal sealed class Logged<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    internal sealed class MemoryReadings : IPhotoReadings
    {
        public Dictionary<string, PhotoReading> Kept { get; } = new();

        public Task<PhotoReading?> FindAsync(string sha256, CancellationToken cancellationToken = default) =>
            Task.FromResult(Kept.TryGetValue(sha256, out var r) ? r : null);

        public Task KeepAsync(string sha256, PhotoReading reading, CancellationToken cancellationToken = default)
        {
            Kept.TryAdd(sha256, reading);
            return Task.CompletedTask;
        }
    }

    private sealed record Rig(ClaudeProofreader Proofreader, FakeApi Api, SquarePhotoFetcherTests.FakeSquare Square, Logged<ClaudeProofreader> Log);

    private static Rig Proofreader(Func<string> reply, ProofreadRequestSettings? settings = null, Func<HttpRequestMessage, HttpResponseMessage>? photos = null)
    {
        var api = new FakeApi(reply);
        var square = new SquarePhotoFetcherTests.FakeSquare(photos ?? (_ => SquarePhotoFetcherTests.FakeSquare.Photo(SquarePhotoFetcherTests.Jpeg)));
        var log = new Logged<ClaudeProofreader>();
        // Built from parts so this file holds nothing shaped like a real key.
        var client = new AnthropicClient { ApiKey = string.Concat("test", "-", "not-a-key"), HttpClient = new HttpClient(api), MaxRetries = 0 };
        return new Rig(new ClaudeProofreader(client, SquarePhotoFetcherTests.Fetcher(square), Instructions, settings ?? Api, log), api, square, log);
    }

    // ---- The request ----

    [Fact]
    public async Task The_request_is_opus_5_5_thinking_adaptively_at_medium_effort_with_the_findings_schema()
    {
        var rig = Proofreader(() => Message(Clean));

        await rig.Proofreader.ProofreadAsync(BackToSchool(), Business());

        var body = Assert.Single(rig.Api.Bodies);
        Assert.Equal("claude-opus-5-5", (string?)body["model"]);
        Assert.Equal(16000, (int?)body["max_tokens"]);
        Assert.Equal("adaptive", (string?)body["thinking"]?["type"]);
        Assert.Equal("medium", (string?)body["output_config"]?["effort"]);
        var format = body["output_config"]?["format"];
        Assert.Equal("json_schema", (string?)format?["type"]);
        Assert.Equal(["findings", "photos"], format!["schema"]!["required"]!.AsArray().Select(n => (string?)n));
        var finding = format["schema"]!["properties"]!["findings"]!["items"]!;
        Assert.Equal(["severity", "category", "block", "quote", "problem", "suggestion"], finding["required"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["must-fix", "worth-a-look"], finding["properties"]!["severity"]!["enum"]!.AsArray().Select(n => (string?)n));
        // Forced tool use and an assistant prefill are refused by Opus 5.5: neither is sent.
        Assert.Null(body["tool_choice"]);
        Assert.Null(body["tools"]);
        Assert.Equal(["user"], body["messages"]!.AsArray().Select(m => (string?)m!["role"]));
    }

    [Fact]
    public async Task The_instructions_are_the_data_file_first_and_the_one_cached_prefix()
    {
        var rig = Proofreader(() => Message(Clean));

        await rig.Proofreader.ProofreadAsync(BackToSchool(), Business());

        var body = rig.Api.Bodies.Single();
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Proofread", "proofread.json")))!;
        var system = Assert.Single(body["system"]!.AsArray())!;
        Assert.Equal(string.Join("\n\n", file["instructions"]!.AsArray().Select(p => (string?)p)), (string?)system["text"]);
        Assert.Equal("ephemeral", (string?)system["cache_control"]?["type"]);
        // The only breakpoint: everything after the instructions is this email's, never cached.
        Assert.Equal(1, CountOf(body.ToJsonString(), "\"cache_control\""));
        var content = body["messages"]![0]!["content"]!.AsArray();
        Assert.Equal((string?)file["request"], (string?)content[^1]!["text"]);
    }

    [Fact]
    public async Task The_cached_prefix_is_the_same_bytes_for_every_email_and_client()
    {
        var rig = Proofreader(() => Message(Clean));

        await rig.Proofreader.ProofreadAsync(BackToSchool(), Business());
        await rig.Proofreader.ProofreadAsync(SampleCampaigns.SecondSend(), new BusinessContext("Another salon", "Elsewhere."));

        Assert.Equal(rig.Api.Bodies[0]["system"]!.ToJsonString(), rig.Api.Bodies[1]["system"]!.ToJsonString());
        Assert.Equal(rig.Api.Bodies[0]["output_config"]!.ToJsonString(), rig.Api.Bodies[1]["output_config"]!.ToJsonString());
    }

    [Fact]
    public async Task On_the_anthropic_api_a_refusal_falls_back_server_side()
    {
        var rig = Proofreader(() => Message(Clean));

        await rig.Proofreader.ProofreadAsync(BackToSchool(), Business());

        Assert.Equal("default", (string?)rig.Api.Bodies.Single()["fallbacks"]);
        Assert.Contains(ClaudeProofreader.ServerSideFallbackBeta, rig.Api.Requests.Single().Headers.GetValues("anthropic-beta").SelectMany(v => v.Split(',')));
    }

    [Fact]
    public async Task Without_server_side_fallback_neither_the_field_nor_the_beta_is_sent()
    {
        var rig = Proofreader(() => Message(Clean), Api with { ServerSideFallback = false });

        await rig.Proofreader.ProofreadAsync(BackToSchool(), Business());

        Assert.Null(rig.Api.Bodies.Single()["fallbacks"]);
        Assert.False(rig.Api.Requests.Single().Headers.Contains("anthropic-beta"));
    }

    [Fact]
    public async Task The_email_goes_block_by_block_by_the_export_s_ids_inside_its_tags()
    {
        var rig = Proofreader(() => Message(Clean));

        await rig.Proofreader.ProofreadAsync(BackToSchool(), Business());

        var text = (string)rig.Api.Bodies.Single()["messages"]![0]!["content"]![0]!["text"]!;
        var email = text[text.IndexOf("<email>", StringComparison.Ordinal)..(text.IndexOf("</email>", StringComparison.Ordinal) + 8)];
        Assert.Contains("[subject]\nBack to school", email);
        Assert.Contains("[b1] Header with the photo \"Principals toasting\", described as \"The principals, toasting\"\nNeelam Aesthetics", email);
        Assert.Contains("[b3] Text\nTeachers get $50 off 30+ units this September.\n\nBook before Septembr 30.", email);
        Assert.Contains("[b4] Image with the photo \"Teacher offer\", which has no description", email);
        Assert.Contains("[b5] Button, linking to https://example.com/book\nBook now", email);
        Assert.Contains("<business>\nName: Neelam Aesthetics\nA family-run clinic.\n</business>", text);
    }

    // The blocks she approves, by the export's ids, each with the campaign blocks it is made from.
    [Fact]
    public void Run_on_text_names_every_block_it_is_made_from_once()
    {
        var campaign = new Campaign("Hi", [
            new GreetingBlock("Greeting", "Hi Beautiful"),
            new ParagraphsBlock("Opening", ["One.", "Two."]),
            new HeadingBlock("Heading", "NEWS"),
            new SignOffBlock("Sign-off", new SignOff("With love,", "The team")),
        ]);

        var parts = EditorExport.PreviewParts(campaign);

        Assert.Equal(["b1", "b2", "b3"], parts.Select(p => p.Id));
        Assert.Equal(["Greeting", "Opening"], parts[0].Labels);
        Assert.Equal("Hi Beautiful\n\nOne.\n\nTwo.", parts[0].Block.Text);
        Assert.Equal(["Heading"], parts[1].Labels);
        Assert.Equal(["Sign-off"], parts[2].Labels);
        Assert.Equal(EditorExport.PreviewBlocks(campaign), parts.Select(p => p.Block));
    }

    [Fact]
    public void A_quote_in_run_on_text_is_placed_in_the_block_that_holds_it()
    {
        var campaign = new Campaign("Hi", [new GreetingBlock("Greeting", "Hi Beautiful"), new ParagraphsBlock("Opening", ["Recieve a gift."])]);
        const string json = """{"findings":[{"severity":"must-fix","category":"spelling","block":"b1","quote":"Recieve","problem":"p","suggestion":"Receive"}],"photos":[]}""";

        var f = Assert.Single(ClaudeProofreader.ToFindings(json, campaign, EditorExport.PreviewParts(campaign), Instructions).Findings);

        Assert.Equal("Opening", f.Location);
    }

    // ---- Photos ----

    [Fact]
    public async Task Photos_are_fetched_here_and_sent_as_their_bytes_never_as_an_address()
    {
        var rig = Proofreader(() => Message(Clean));

        var result = await rig.Proofreader.ProofreadAsync(BackToSchool(), Business());

        var body = rig.Api.Bodies.Single().ToJsonString();
        var images = rig.Api.Bodies.Single()["messages"]![0]!["content"]!.AsArray().Where(b => (string?)b!["type"] == "image").ToList();
        Assert.Equal(2, images.Count);
        Assert.All(images, i =>
        {
            Assert.Equal("base64", (string?)i!["source"]!["type"]);
            Assert.Equal("image/jpeg", (string?)i["source"]!["media_type"]);
            Assert.Equal(Convert.ToBase64String(SquarePhotoFetcherTests.Jpeg), (string?)i["source"]!["data"]);
        });
        Assert.DoesNotContain("squarecdn", body);
        Assert.DoesNotContain("\"url\"", body);
        Assert.Equal([new Uri(HeaderPhoto), new Uri(OfferPhoto)], rig.Square.Asked);
        Assert.Equal(
            [new PhotoCheck("Principals toasting", ClaudeProofreader.ReadNow, true), new PhotoCheck("Teacher offer", ClaudeProofreader.ReadNow, true)],
            result.Photos);
    }

    [Fact]
    public async Task A_photo_off_the_allowlist_is_not_fetched_and_the_result_says_so()
    {
        var rig = Proofreader(() => Message(Clean));
        var business = Business() with
        {
            PhotoAddresses = new Dictionary<string, Uri>
            {
                ["Principals toasting"] = new("https://postoffice-production-f.squarecdn.com.evil.test/header.jpg"),
                ["Teacher offer"] = new(OfferPhoto),
            },
        };

        var result = await rig.Proofreader.ProofreadAsync(BackToSchool(), business);

        Assert.Equal([new Uri(OfferPhoto)], rig.Square.Asked);
        Assert.Contains(new PhotoCheck("Principals toasting", "Couldn't check this photo: not a Square address.", false), result.Photos);
        Assert.Single(rig.Api.Bodies.Single()["messages"]![0]!["content"]!.AsArray(), b => (string?)b!["type"] == "image");
    }

    [Fact]
    public async Task A_photo_not_in_the_library_or_refused_by_square_never_stops_the_proofread()
    {
        var rig = Proofreader(() => Message(Clean), photos: _ => SquarePhotoFetcherTests.FakeSquare.Photo(SquarePhotoFetcherTests.Jpeg, "text/html"));
        var business = Business() with { PhotoAddresses = new Dictionary<string, Uri> { ["Teacher offer"] = new(OfferPhoto) } };

        var result = await rig.Proofreader.ProofreadAsync(BackToSchool(), business);

        Assert.Equal(
            [
                new PhotoCheck("Principals toasting", "Couldn't check this photo: it is not a Square photo in your image library.", false),
                new PhotoCheck("Teacher offer", "Couldn't check this photo: Square did not send a photo.", false),
            ],
            result.Photos);
        Assert.Single(rig.Api.Bodies);
        Assert.DoesNotContain(rig.Api.Bodies.Single()["messages"]![0]!["content"]!.AsArray(), b => (string?)b!["type"] == "image");
    }

    [Fact]
    public async Task A_photo_seen_before_goes_as_its_reading_and_a_changed_one_is_read_again()
    {
        var readings = new MemoryReadings();
        var bytes = SquarePhotoFetcherTests.Jpeg;
        var answer = """{"findings":[],"photos":[{"photo":"p1","words":"Neelam","shows":"Two principals toasting.","offers":""},{"photo":"p2","words":"$50 OFF 30+ UNITS","shows":"A teacher offer card.","offers":"$50 off 30+ units, September 1-30"}]}""";
        var rig = Proofreader(() => Message(answer), photos: r => SquarePhotoFetcherTests.FakeSquare.Photo(r.RequestUri!.AbsolutePath == "/offer.jpg" ? bytes : SquarePhotoFetcherTests.Jpeg));

        await rig.Proofreader.ProofreadAsync(BackToSchool(), Business(readings));
        Assert.Single(readings.Kept); // Both photos are the same bytes: one reading, by their one hash.

        // The same bytes again: nothing sent as an image, the reading given as text instead.
        var second = await rig.Proofreader.ProofreadAsync(BackToSchool(), Business(readings));
        var content = rig.Api.Bodies[1]["messages"]![0]!["content"]!.AsArray();
        Assert.DoesNotContain(content, b => (string?)b!["type"] == "image");
        Assert.Contains(content, b => ((string?)b!["text"])?.Contains("read-before=\"yes\">\nWords in it: Neelam\nWhat it shows: Two principals toasting.") == true);
        Assert.All(second.Photos, p => Assert.Equal(ClaudeProofreader.ReadBefore, p.Note));

        // The offer photo changed at the same address: a new hash, so it is read again.
        bytes = [0xFF, 0xD8, 0xFF, 0xE1, 9, 9, 9];
        var third = await rig.Proofreader.ProofreadAsync(BackToSchool(), Business(readings));
        var images = rig.Api.Bodies[2]["messages"]![0]!["content"]!.AsArray().Where(b => (string?)b!["type"] == "image").ToList();
        Assert.Equal(Convert.ToBase64String(bytes), (string?)Assert.Single(images)!["source"]!["data"]);
        Assert.Equal([ClaudeProofreader.ReadBefore, ClaudeProofreader.ReadNow], third.Photos.Select(p => p.Note));
        Assert.Equal(2, readings.Kept.Count);
    }

    [Fact]
    public async Task Readings_are_kept_by_the_hash_of_the_bytes()
    {
        var readings = new MemoryReadings();
        var rig = Proofreader(() => Message("""{"findings":[],"photos":[{"photo":"p1","words":"w","shows":"s","offers":"o"}]}"""));

        await rig.Proofreader.ProofreadAsync(BackToSchool(), Business(readings));

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(SquarePhotoFetcherTests.Jpeg));
        Assert.Equal(new PhotoReading("w", "s", "o"), readings.Kept[hash]);
    }

    // ---- The answer ----

    [Fact]
    public async Task A_must_fix_is_a_blocker_placed_in_its_campaign_block()
    {
        var rig = Proofreader(() => Message("""
            {"findings":[{"severity":"must-fix","category":"spelling","block":"b3","quote":"Septembr",
              "problem":"Misspelt month.","suggestion":"September"}],"photos":[]}
            """));

        var f = Assert.Single((await rig.Proofreader.ProofreadAsync(BackToSchool(), Business())).Findings);

        Assert.Equal(Severity.Blocker, f.Severity);
        Assert.Equal("ai-spelling", f.Rule);
        Assert.Equal("Opening", f.Location);
        Assert.Equal("Septembr", f.Excerpt);
        Assert.Equal("Misspelt month. Suggest: September", f.Message);
    }

    [Theory]
    [InlineData("subject", "Back to", "Subject")]
    [InlineData("b2", "BACK TO SCHOOL", "Heading")]
    [InlineData("b9", "Book now", "Whole email")]
    public void Each_finding_is_placed_where_the_page_can_take_her(string block, string quote, string location)
    {
        var campaign = BackToSchool();
        var json = $$"""{"findings":[{"severity":"worth-a-look","category":"clarity","block":"{{block}}","quote":"{{quote}}","problem":"p","suggestion":"s"}],"photos":[]}""";

        var f = Assert.Single(ClaudeProofreader.ToFindings(json, campaign, EditorExport.PreviewParts(campaign), Instructions).Findings);

        Assert.Equal(location, f.Location);
    }

    [Fact]
    public void A_quote_not_in_the_email_is_downgraded_not_trusted()
    {
        var campaign = BackToSchool();
        const string json = """{"findings":[{"severity":"must-fix","category":"spelling","block":"b3","quote":"recieve your gift","problem":"Misspelling.","suggestion":"receive"}],"photos":[]}""";

        var f = Assert.Single(ClaudeProofreader.ToFindings(json, campaign, EditorExport.PreviewParts(campaign), Instructions).Findings);

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.EndsWith("(The quote is not in the email; check by hand.)", f.Message);
    }

    [Fact]
    public void A_quote_from_a_photo_says_to_check_it_there()
    {
        var campaign = BackToSchool();
        const string json = """{"findings":[{"severity":"must-fix","category":"photo","block":"b4","quote":"$40 OFF","problem":"The photo says $40; the text says $50.","suggestion":"Make them agree."}],"photos":[]}""";

        var f = Assert.Single(ClaudeProofreader.ToFindings(json, campaign, EditorExport.PreviewParts(campaign), Instructions).Findings);

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Offer photo", f.Location);
        Assert.EndsWith("(Quoted from the photo: check it there.)", f.Message);
    }

    [Fact]
    public async Task Empty_findings_is_a_clean_proofread()
    {
        var rig = Proofreader(() => Message(Clean));

        Assert.Empty((await rig.Proofreader.ProofreadAsync(BackToSchool(), Business())).Findings);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"findings":[{"severity":"must-fix"]}""")]
    [InlineData("""{"photos":[]}""")]
    [InlineData("""{"findings":[null],"photos":[]}""")]
    [InlineData("")]
    public async Task An_answer_that_cannot_be_read_says_so_and_is_never_a_clean_proofread(string answer)
    {
        var rig = Proofreader(() => Message(answer));

        var ex = await Assert.ThrowsAsync<ProofreadUnavailableException>(() => rig.Proofreader.ProofreadAsync(BackToSchool(), Business()));

        Assert.Equal("The AI proofread's answer could not be read, so nothing it found is shown.", ex.Message);
    }

    [Fact]
    public async Task A_refusal_says_so_plainly_with_its_category()
    {
        var rig = Proofreader(() => Message("", "refusal", """{"type":"refusal","category":"bio","explanation":null}"""));

        var ex = await Assert.ThrowsAsync<ProofreadUnavailableException>(() => rig.Proofreader.ProofreadAsync(BackToSchool(), Business()));

        Assert.Equal(
            "The AI proofread declined to read this version (its safety check named \"bio\"). That says nothing about the email itself; read it through yourself before it goes out.",
            ex.Message);
    }

    [Fact]
    public async Task An_answer_cut_off_says_so()
    {
        var rig = Proofreader(() => Message("""{"findings":[""", "max_tokens"));

        var ex = await Assert.ThrowsAsync<ProofreadUnavailableException>(() => rig.Proofreader.ProofreadAsync(BackToSchool(), Business()));

        Assert.Equal("The AI proofread was cut off before it finished, so nothing it found is shown.", ex.Message);
    }

    [Fact]
    public async Task An_api_that_cannot_be_reached_says_so()
    {
        var api = new FailingApi();
        var client = new AnthropicClient { ApiKey = string.Concat("test", "-", "not-a-key"), HttpClient = new HttpClient(api), MaxRetries = 0 };
        var proofreader = new ClaudeProofreader(client, SquarePhotoFetcherTests.Fetcher(new SquarePhotoFetcherTests.FakeSquare(_ => SquarePhotoFetcherTests.FakeSquare.Photo(SquarePhotoFetcherTests.Jpeg))),
            Instructions, Api, new Logged<ClaudeProofreader>());

        var ex = await Assert.ThrowsAsync<ProofreadUnavailableException>(() => proofreader.ProofreadAsync(BackToSchool(), Business()));

        Assert.Equal("The AI proofread could not be reached just now. Try again in a while.", ex.Message);
    }

    private sealed class FailingApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"type":"error","error":{"type":"api_error","message":"boom"}}""", Encoding.UTF8, "application/json"),
            });
    }

    // ---- What is logged ----

    [Fact]
    public async Task Token_counts_are_logged_cache_reads_included_and_never_the_email_or_findings()
    {
        var rig = Proofreader(() => Message("""
            {"findings":[{"severity":"must-fix","category":"spelling","block":"b3","quote":"Septembr","problem":"Misspelt month.","suggestion":"September"}],
             "photos":[{"photo":"p1","words":"SECRET PHOTO WORDS","shows":"x","offers":""}]}
            """, cacheRead: 1234));

        await rig.Proofreader.ProofreadAsync(BackToSchool(), Business());

        var line = Assert.Single(rig.Log.Lines);
        Assert.Contains("tokens in 4321, out 876, cache read 1234, cache written 0", line);
        Assert.Contains("photos sent 2", line);
        foreach (var content in new[] { "Septembr", "Teachers", "Neelam", "family-run", "SECRET PHOTO WORDS", "Misspelt", "Principals", "squarecdn" })
            Assert.DoesNotContain(content, line);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
