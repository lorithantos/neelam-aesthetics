using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The activity trail (owner, 2026-10-09): "We want as much tracking as we can - deletions too are
/// recorded, but not the data." One event per action, in the acting client's partition, naming
/// things only by id, never by what they say; and a trail that cannot be written never stops the
/// action it records.
/// </summary>
public class ActivityTests
{
    private static readonly ClientName Salon = new("test-salon-one");
    private static readonly ClientName Other = new("other-salon");
    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);
    private static readonly Actor Asha = new("Asha Patel", FromSignIn: true);

    private const string Photo = "Principals toasting";
    private const string PhotoAddress =
        "https://postoffice-production-f.squarecdn.com/a7d8bc0e/principals-toasting.jpeg?enable=upscale&height=196&width=640";
    private const string Label = "Beauty Bank -- first send";

    private static readonly ClientRecord Registration =
        new(Salon, Guid.Parse("11111111-2222-3333-4444-555555555555"), "Neelam Aesthetics",
            "A medical aesthetics clinic in Snohomish, WA, offering injectables and facials.")
        {
            Phones = new PhoneNumbers([PhoneNumber.FromDigits("14258778646")!]),
        };

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryContainers _containers = new();
    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private ClientStores Stores => Records.Stores(_containers.For, Grace);

    private IReadOnlyList<(ActivityEntity, ActivityAction)> Trail(IEnumerable<ActivityEvent>? events = null) =>
        (events ?? Records.Activity.Events).Select(e => (e.Entity, e.Action)).ToList();

    private static string Stamp(SaveRef save) => save.BlobName.Split('/')[2][..^".json".Length];

    [Fact]
    public async Task Each_campaign_action_is_one_event_naming_the_save_and_who_did_it()
    {
        var store = Stores.Campaigns(Salon, Asha);
        var id = Guid.NewGuid();

        var first = await store.SaveDraftAsync(id, "WE’RE TURNING ONE!", DraftFixtures.Finished());
        _clock.Now += TimeSpan.FromMinutes(1);
        await store.ApproveAsync(first, "Priya");
        _clock.Now += TimeSpan.FromMinutes(1);
        await store.WithdrawApprovalAsync(first);
        await store.MarkUndoneAsync(first);
        Assert.True(await store.RestoreAsync(first));
        await store.MarkUndoneAsync(first);
        _clock.Now += Grace + TimeSpan.FromMinutes(1);
        Assert.Equal(1, await Stores.Campaigns(Salon, Actor.UndoSweep).SweepAsync());

        Assert.Equal(
            [
                (ActivityEntity.Campaign, ActivityAction.Saved),
                (ActivityEntity.Campaign, ActivityAction.Approved),
                (ActivityEntity.Campaign, ActivityAction.ApprovalWithdrawn),
                (ActivityEntity.Campaign, ActivityAction.Undone),
                (ActivityEntity.Campaign, ActivityAction.Restored),
                (ActivityEntity.Campaign, ActivityAction.Undone),
                (ActivityEntity.Campaign, ActivityAction.DeletedBySweep),
            ],
            Trail());
        var events = Records.Activity.Events;
        Assert.All(events, e =>
        {
            Assert.Equal(Salon, e.Client);
            Assert.Equal(id.ToString("N"), e.EntityId);
            Assert.Equal(Stamp(first), e.SaveStamp);
        });
        // Signed in, the approval is the signed-in user's on the trail, whatever name was typed.
        Assert.Equal(["Asha Patel", "Asha Patel", "Asha Patel", "Asha Patel", "Asha Patel", "Asha Patel", "undo sweep"],
            events.Select(e => e.Actor));
        // The server's time, from its clock.
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), events[0].At);
        Assert.Equal(_clock.Now, events[^1].At);
    }

    [Fact]
    public async Task Each_template_action_is_one_event_too()
    {
        var store = Stores.Campaigns(Salon, Asha);
        var id = Guid.NewGuid();

        var saved = await store.SaveTemplateAsync(id, DraftFixtures.Membership);
        await store.MarkUndoneAsync(saved);
        await store.RestoreAsync(saved);
        await store.MarkUndoneAsync(saved);
        _clock.Now += Grace + TimeSpan.FromMinutes(1);
        await Stores.Campaigns(Salon, Actor.UndoSweep).SweepAsync();

        Assert.Equal(
            [
                (ActivityEntity.Template, ActivityAction.Saved),
                (ActivityEntity.Template, ActivityAction.Undone),
                (ActivityEntity.Template, ActivityAction.Restored),
                (ActivityEntity.Template, ActivityAction.Undone),
                (ActivityEntity.Template, ActivityAction.DeletedBySweep),
            ],
            Trail());
        Assert.All(Records.Activity.Events, e => Assert.Equal((id.ToString("N"), Stamp(saved)), (e.EntityId, e.SaveStamp)));
    }

    // Nothing is recorded for what did not happen: a refused approval, a restore past its grace.
    [Fact]
    public async Task Refused_actions_record_nothing()
    {
        var store = Stores.Campaigns(Salon, Asha);
        var saved = await store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", DraftFixtures.Finished());
        await store.MarkUndoneAsync(saved);
        var before = Records.Activity.Events.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApproveAsync(saved, "Priya"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApproveAsync(saved, " "));
        _clock.Now += Grace + TimeSpan.FromMinutes(1);
        Assert.False(await store.RestoreAsync(saved));
        Assert.Equal(before, Records.Activity.Events.Count);

        await Registry.AddAsync(Registration, Asha);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Registry.AddAsync(Registration, Asha));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Registry.UpdateAsync(Registration with { Name = Other }, Asha));
        Assert.Equal(before + 1, Records.Activity.Events.Count);
        Assert.Equal(ActivityAction.ClientRegistered, Records.Activity.Events[^1].Action);
    }

    private InMemoryClientDirectory? _directory;
    private InMemoryClientDirectory Directory => _directory ??= new();
    private ClientRegistry Registry => new(Directory, Records.Recorder);

    [Fact]
    public async Task Baseline_image_and_registration_actions_are_one_event_each()
    {
        await Stores.SaveBaselineAsync(Salon, TemplateBaseline.None, Asha);
        await Stores.UseStandardBaselineAsync(Salon, Asha);
        var library = Stores.Images(Salon, Asha);
        await library.AddFromSquareAsync(Photo, PhotoAddress);
        await library.AddAsync("Front desk", new BinaryData(new byte[] { 1 }), "image/png");
        await library.DeleteAsync(Photo);
        await Registry.AddAsync(Registration, Asha);
        await Registry.UpdateAsync(Registration with { DisplayName = "Neelam" }, Asha);

        Assert.Equal(
            [
                (ActivityEntity.Baseline, ActivityAction.BaselineSaved),
                (ActivityEntity.Baseline, ActivityAction.BaselineResetToStandard),
                (ActivityEntity.ImageEntry, ActivityAction.ImageEntryAdded),
                (ActivityEntity.ImageEntry, ActivityAction.ImageEntryAdded),
                (ActivityEntity.ImageEntry, ActivityAction.ImageEntryRemoved),
                (ActivityEntity.ClientRegistration, ActivityAction.ClientRegistered),
                (ActivityEntity.ClientRegistration, ActivityAction.ClientChanged),
            ],
            Trail());
        var events = Records.Activity.Events;
        Assert.All(events, e => Assert.Equal((Salon, "Asha Patel"), (e.Client, e.Actor)));
        Assert.Equal(["baseline", "baseline"], events.Take(2).Select(e => e.EntityId));
        Assert.Matches("^[0-9]{8}T[0-9]{6}\\.[0-9]{7}Z$", events[0].SaveStamp);
        Assert.Null(events[1].SaveStamp);
        // An entry is named by its own random id, the same on adding and removing, never by its name.
        Assert.Matches("^[0-9a-f]{32}$", events[2].EntityId);
        Assert.Equal(events[2].EntityId, events[4].EntityId);
        Assert.NotEqual(events[2].EntityId, events[3].EntityId);
        Assert.Equal([Salon.Value, Salon.Value], events.Skip(5).Select(e => e.EntityId));
    }

    // In Prototype nobody signs in: an approval goes against the name typed for it, and everything
    // else against "demo user".
    [Fact]
    public async Task Prototype_records_the_typed_approver_or_demo_user()
    {
        var store = Stores.Campaigns(Salon, Actor.Demo);
        var saved = await store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", DraftFixtures.Finished());
        await store.ApproveAsync(saved, "  Priya Śharma ");
        await store.WithdrawApprovalAsync(saved);

        Assert.Equal(["demo user", "Priya Śharma", "demo user"], Records.Activity.Events.Select(e => e.Actor));
        Assert.Equal("Asha Patel", Asha.NameFor("Priya"));
        Assert.Equal("Priya", Actor.Demo.NameFor(" Priya "));
        Assert.Equal("demo user", Actor.Demo.NameFor("  "));
    }

    // Each client's events are in its own partition, so they sit under that client's access rules.
    [Fact]
    public async Task Events_are_kept_in_the_acting_client_s_partition()
    {
        await Stores.Campaigns(Salon, Asha).SaveDraftAsync(Guid.NewGuid(), "One", DraftFixtures.Finished());
        await Stores.Campaigns(Other, Asha).SaveDraftAsync(Guid.NewGuid(), "Two", DraftFixtures.Finished());
        await Stores.Images(Other, Asha).AddFromSquareAsync(Photo, PhotoAddress);

        Assert.Equal([Salon, Other, Other], Records.Activity.Events.Select(e => e.Client));
        Assert.Equal([Salon.Value, Other.Value, Other.Value],
            Records.Activity.Events.Select(e => TableMetadata.FromActivity(e, Guid.NewGuid()).PartitionKey));
    }

    // The trail is wanted, but never at the cost of the action: with the activity table down, every
    // action still happens, and each failed write is logged, naming no content.
    [Fact]
    public async Task A_failed_event_write_never_fails_the_action()
    {
        Records.Activity.Fail = true;
        var store = Stores.Campaigns(Salon, Asha);
        var id = Guid.NewGuid();

        var saved = await store.SaveDraftAsync(id, "WE’RE TURNING ONE!", DraftFixtures.Finished());
        var approved = await store.ApproveAsync(saved, "Priya");
        await store.WithdrawApprovalAsync(saved);
        await store.MarkUndoneAsync(saved);
        Assert.True(await store.RestoreAsync(saved));
        await store.MarkUndoneAsync(saved);
        _clock.Now += Grace + TimeSpan.FromMinutes(1);
        Assert.Equal(1, await Stores.Campaigns(Salon, Actor.UndoSweep).SweepAsync());
        await Stores.SaveBaselineAsync(Salon, TemplateBaseline.None, Asha);
        await Stores.UseStandardBaselineAsync(Salon, Asha);
        var library = Stores.Images(Salon, Asha);
        await library.AddFromSquareAsync(Photo, PhotoAddress);
        Assert.True(await library.DeleteAsync(Photo));
        await Registry.AddAsync(Registration, Asha);
        await Registry.UpdateAsync(Registration, Asha);

        // Every action happened.
        Assert.NotNull(approved.Approval);
        Assert.Empty(await store.HistoryAsync(DocumentKind.Draft, id));
        Assert.Null(await Stores.Baseline(Salon).CurrentAsync());
        Assert.Empty(await library.ListAsync());
        Assert.Single(await Directory.ListAsync());
        // Nothing reached the trail, and each of the 13 failures was logged as an error.
        Assert.Empty(Records.Activity.Events);
        var errors = Records.Log.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Equal(13, errors.Count);
        Assert.All(errors, e => Assert.IsType<Azure.RequestFailedException>(e.Error));
        Assert.All(errors, e => Assert.DoesNotContain(Photo, e.Message));
    }

    // The app wires its stores to the approvals table and the activity table, and the sweep it runs
    // records against "undo sweep".
    [Fact]
    public async Task The_app_records_through_its_own_tables()
    {
        await using var app = new DemoApp();
        var store = app.Stores.Campaigns(DemoApp.Client, Actor.Demo);
        var saved = await store.SaveDraftAsync(Guid.NewGuid(), "WE’RE TURNING ONE!", DraftFixtures.Finished());
        await store.ApproveAsync(saved, "Priya");
        await store.MarkUndoneAsync(saved);
        app.Clock.Now += TimeSpan.FromDays(2);

        await app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Neelam.Web.UndoSweep>().Single()
            .SweepOnceAsync();

        Assert.Single(app.Approvals.Rows);
        Assert.Equal(
            [(ActivityAction.Saved, "demo user"), (ActivityAction.Approved, "Priya"), (ActivityAction.Undone, "demo user"),
                (ActivityAction.DeletedBySweep, "undo sweep")],
            app.Activity.Events.Select(e => (e.Action, e.Actor)));
    }

    // THE owner's rule for the trail: no content, ever. A whole flow through every action, with real
    // campaign content, a label, a registration and a photo; then every event, as the table row it
    // becomes, is scanned for any of that text, and every field must have the shape of an id, a
    // stamp, a kind, an action or an actor.
    [Fact]
    public async Task No_event_ever_holds_campaign_text()
    {
        await Registry.AddAsync(Registration, Asha);
        await Registry.UpdateAsync(Registration with { Description = "Now with a booking line." }, Asha);
        var store = Stores.Campaigns(Salon, Asha);
        var templateId = Guid.NewGuid();
        await store.SaveTemplateAsync(templateId, DraftFixtures.Membership);
        _clock.Now += TimeSpan.FromMinutes(1);

        var draft = DraftFixtures.SecondSendReplayed();
        draft.Label = Label;
        var id = Guid.NewGuid();
        await store.SaveDraftAsync(id, "WE’RE TURNING ONE!", draft);
        _clock.Now += TimeSpan.FromMinutes(1);
        var session = (await DraftSession.OpenAsync(store, id))!;
        session.Editor.Subject.Text = "WE’RE TURNING ONE!";
        await session.SaveAsync();
        var finished = DraftFixtures.Finished();
        finished.Label = Label;
        var saved = await store.SaveDraftAsync(id, "WE’RE TURNING ONE!", finished);
        await store.ApproveAsync(saved, "Priya");
        await store.WithdrawApprovalAsync(saved);
        await store.ApproveAsync(saved, "Priya");
        await store.MarkUndoneAsync(saved);
        await store.RestoreAsync(saved);
        await store.MarkUndoneAsync(saved);
        _clock.Now += Grace + TimeSpan.FromMinutes(1);
        await Stores.Campaigns(Salon, Actor.UndoSweep).SweepAsync();
        await Stores.SaveBaselineAsync(Salon, TemplateBaseline.Standard, Asha);
        await Stores.UseStandardBaselineAsync(Salon, Asha);
        var library = Stores.Images(Salon, Asha);
        await library.AddFromSquareAsync(Photo, PhotoAddress);
        await library.DeleteAsync(Photo);

        var events = Records.Activity.Events;
        Assert.Equal(Enum.GetValues<ActivityAction>().Order(), events.Select(e => e.Action).Distinct().Order());

        var content = ContentOf(draft, finished, DraftFixtures.Membership);
        Assert.Contains(Label, content);
        Assert.Contains("Gold Member", content);
        Assert.Contains("149", content);
        foreach (var activity in events)
        {
            var row = TableMetadata.FromActivity(activity, Guid.NewGuid());
            var values = row.Where(p => p.Key is not ("odata.etag" or "Timestamp"))
                .Select(p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToList();
            Assert.Empty(
                from text in content.Where(t => !t.All(char.IsAsciiDigit))
                from value in values
                where value.Contains(text, StringComparison.OrdinalIgnoreCase)
                select $"{text} in {value}");
            // Numbers, such as prices, against the fields that could carry words.
            foreach (var number in content.Where(t => t.All(char.IsAsciiDigit)))
                Assert.DoesNotContain(number, $"{activity.Actor} {activity.Entity} {activity.Action}");

            Assert.Matches(EntityIdShape, activity.EntityId);
            if (activity.SaveStamp is not null) Assert.Matches("^[0-9]{8}T[0-9]{6}\\.[0-9]{7}Z$", activity.SaveStamp);
            Assert.Contains(activity.Actor, new[] { "Asha Patel", "undo sweep" });
            Assert.Equal(
                ["Action", "Actor", "At", "Entity", "EntityId", "PartitionKey", "RowKey", .. activity.SaveStamp is null ? Array.Empty<string>() : ["SaveStamp"]],
                row.Keys.Where(k => k is not ("odata.etag" or "Timestamp")).Order(StringComparer.Ordinal));
        }
    }

    // A campaign or template id, a photo entry's random id, the baseline, or a client's name.
    private static readonly Regex EntityIdShape = new("^(?:[0-9a-f]{32}|baseline|test-salon-one)$");

    // Every text and number a person typed: each string and number in the drafts' and template's
    // JSON (a field's kind, such as a block type, is a name the code gives, not text), and the
    // registration and the photo.
    private static List<string> ContentOf(params object[] documents)
    {
        var kinds = Enum.GetNames<BlockType>().Concat(Enum.GetNames<Origin>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                // A value's kind, type or origin is the code's vocabulary, not what anyone wrote.
                case JsonObject o:
                    foreach (var (key, v) in o)
                        if (key is not ("kind" or "type" or "origin")) Walk(v);
                    break;
                case JsonArray a: foreach (var v in a) Walk(v); break;
                case JsonValue v when v.TryGetValue<string>(out var s): found.Add(s); break;
                case JsonValue v: found.Add(v.ToJsonString()); break;
            }
        }
        foreach (var document in documents)
        {
            Walk(JsonNode.Parse(document switch
            {
                CampaignDraft d => CampaignJson.SerializeDraft(d),
                CampaignTemplate t => CampaignJson.SerializeTemplate(t),
                _ => throw new ArgumentException("A draft or a template.", nameof(documents)),
            }));
        }
        found.AddRange([Registration.DisplayName, Registration.Description!, "Now with a booking line.", "4258778646", "(425) 877-8646",
            Photo, PhotoAddress, "squarecdn"]);
        return found.Select(s => s.Trim()).Where(s => s.Length >= 3 && !kinds.Contains(s) && s is not ("true" or "false" or "null"))
            .Distinct().ToList();
    }
}
