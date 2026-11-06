using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// The Known items page as it works: one client's items, the forms to add each kind, and one item
/// open for changing at a time. The Razor page only binds to this, so what it does is tested
/// without a browser. Every change is checked by the store (<see cref="KnownItemStoreRules"/>), put
/// on the activity trail by the item's id alone, never its text, and the list read again, so the
/// page shows what is stored. The client is the trail's: the one the access check gave the page.
/// </summary>
public sealed class KnownItemsSession
{
    /// <summary>What the campaign editor says when the store fails: her campaign is still in the form.</summary>
    public const string CampaignSaveFailed =
        "Couldn't save that to your known items. Your campaign is untouched. Try again in a moment.";

    /// <summary>What this page says when the store fails to save: what she typed is still in the form.</summary>
    public const string SaveFailed =
        "Couldn't save that to your known items. What you typed is still here. Try again in a moment.";

    /// <summary>What this page says when the store fails to remove an item.</summary>
    public const string RemoveFailed = "Couldn't remove that from your known items. Try again in a moment.";

    private readonly IKnownItemStore _store;
    private readonly ActivityTrail _trail;
    private readonly ILogger _log;

    private KnownItemsSession(IKnownItemStore store, ActivityTrail trail, KnownItems items, ILogger log)
    {
        _store = store;
        _trail = trail;
        _log = log;
        Items = items;
    }

    /// <param name="log">
    /// Where a storage failure is logged, by the item's id and the client, never its text. A failure
    /// to save, change or remove is told to her and logged, never thrown: on an interactive page an
    /// exception would end the connection, and with it whatever she had typed.
    /// </param>
    public static async Task<KnownItemsSession> OpenAsync(
        IKnownItemStore store, ActivityTrail trail, ILogger? log = null, CancellationToken ct = default) =>
        new(store, trail, await store.ForClientAsync(trail.Client, ct), log ?? NullLogger.Instance);

    public KnownItems Items { get; private set; }

    /// <summary>What the last action did, for a status line.</summary>
    public string? Message { get; private set; }

    /// <summary>Why the last action could not be done; empty when it was.</summary>
    public IReadOnlyList<string> Errors { get; private set; } = [];

    // ---- Adding ----

    /// <summary>The new treatment's name and optional regular price.</summary>
    public KnownTreatmentForm NewTreatment { get; private set; } = new();

    /// <summary>The new line's kind, words and usual amounts.</summary>
    public BenefitEditor NewBenefit { get; private set; } = BenefitEditor.Standalone();

    /// <summary>The new line's optional lowest and highest amounts.</summary>
    public AmountLimitsForm NewLimits { get; private set; } = new();

    public KnownTierForm NewTier { get; private set; } = new();

    public async Task<bool> AddTreatmentAsync(CancellationToken ct = default)
    {
        var (treatment, errors) = NewTreatment.ToItem(KnownItem.NewId());
        if (treatment is null) return Refuse([.. errors]);
        if (!await ChangeAsync(treatment, adding: true, ct)) return false;
        NewTreatment = new KnownTreatmentForm();
        return true;
    }

    public async Task<bool> AddBenefitAsync(CancellationToken ct = default)
    {
        var (benefit, errors) = NewLimits.ToItem(KnownItem.NewId(), NewBenefit, "Choose what kind of benefit, and fill it in.");
        if (benefit is null) return Refuse([.. errors]);
        if (!await ChangeAsync(benefit, adding: true, ct)) return false;
        (NewBenefit, NewLimits) = (BenefitEditor.Standalone(), new AmountLimitsForm());
        return true;
    }

    public async Task<bool> AddTierAsync(CancellationToken ct = default)
    {
        var (tier, errors) = NewTier.ToItem(KnownItem.NewId());
        if (tier is null) return Refuse([.. errors]);
        if (!await ChangeAsync(tier, adding: true, ct)) return false;
        NewTier = new KnownTierForm();
        return true;
    }

    // ---- Changing one item ----

    /// <summary>The item open for changing, or null.</summary>
    public KnownItem? Editing { get; private set; }

    public KnownTreatmentForm? EditTreatment { get; private set; }
    public BenefitEditor? EditBenefit { get; private set; }
    public AmountLimitsForm? EditLimits { get; private set; }
    public KnownTierForm? EditTier { get; private set; }

    public void StartEdit(KnownItem item)
    {
        Editing = item;
        (Message, Errors) = (null, []);
        EditTreatment = item is KnownTreatment t ? KnownTreatmentForm.Of(t) : null;
        EditBenefit = item is KnownBenefit b ? BenefitEditor.Standalone(b.Benefit) : null;
        EditLimits = item is KnownBenefit line ? AmountLimitsForm.Of(line) : null;
        EditTier = item is KnownTier tier ? KnownTierForm.Of(tier) : null;
        EditPromotion = item is KnownPromotion promotion ? KnownPromotionForm.Of(promotion, Items) : null;
    }

    public void CancelEdit()
    {
        Editing = null;
        (EditBenefit, EditLimits, EditTier, EditTreatment, EditPromotion) = (null, null, null, null, null);
        Errors = [];
    }

    public async Task<bool> SaveEditAsync(CancellationToken ct = default)
    {
        if (Editing is not { } editing) return false;
        KnownItem? changed;
        switch (editing)
        {
            case KnownTreatment:
                var (treatment, wrong) = EditTreatment!.ToItem(editing.Id);
                if (treatment is null) return Refuse([.. wrong]);
                changed = treatment;
                break;
            case KnownBenefit:
                var (line, problems) = EditLimits!.ToItem(editing.Id, EditBenefit!, "Fill in the benefit.");
                if (line is null) return Refuse([.. problems]);
                changed = line;
                break;
            case KnownPromotion:
                var (plan, planErrors) = EditPromotion!.ToPlan(editing.Id, Items);
                if (plan is null) return Refuse([.. planErrors]);
                if (!await ApplyAsync(plan, adding: false, ct)) return false;
                CancelEdit();
                return true;
            default:
                var (tier, errors) = EditTier!.ToItem(editing.Id);
                if (tier is null) return Refuse([.. errors]);
                changed = tier;
                break;
        }
        if (!await ChangeAsync(changed, adding: false, ct)) return false;
        CancelEdit();
        return true;
    }

    // ---- A benefit line's caps, in place ----

    // Per line id, the caps boxes as she has them: kept while she types and after a refused save,
    // dropped once the line changes, so they are built again from what is stored.
    private readonly Dictionary<string, AmountCapsForm> _caps = [];

    /// <summary>
    /// The usual, lowest and highest boxes shown beside <paramref name="line"/> (owner, 2026-10-09:
    /// the caps "easy to find and update"), holding what she typed, else what is kept.
    /// </summary>
    public AmountCapsForm Caps(KnownBenefit line)
    {
        if (!_caps.TryGetValue(line.Id, out var form)) _caps[line.Id] = form = AmountCapsForm.Of(line);
        return form;
    }

    /// <summary>
    /// Saves the caps typed beside <paramref name="line"/> through the same change as the line's own
    /// form: held by the store, one <see cref="ActivityAction.KnownItemChanged"/> event by the id, and
    /// the list read again. When refused, says why beside the boxes and keeps what she typed.
    /// </summary>
    public async Task<bool> SaveCapsAsync(KnownBenefit line, CancellationToken ct = default)
    {
        var form = Caps(line);
        form.Saved = null;
        var (changed, problems) = form.ToItem(line);
        if (changed is null)
        {
            (Message, Errors, form.Errors) = (null, [], problems);
            return false;
        }
        if (!await ChangeAsync(changed, adding: false, ct))
        {
            // Told beside the boxes, where she is looking, not at the top of the page.
            (form.Errors, Errors) = (Errors, []);
            return false;
        }
        // The boxes again from what was saved, with a word that it was.
        Caps(changed).Saved = "Saved.";
        return true;
    }

    // ---- Removing ----

    public async Task RemoveAsync(KnownItem item, CancellationToken ct = default)
    {
        try
        {
            await _store.RemoveAsync(_trail.Client, item.Id, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The store's own refusal, such as a treatment a saved promotion uses: said, nothing recorded.
            Refuse(ex.Message);
            return;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            LogFailure(_log, ex, "remove", item, _trail.Client);
            Refuse(RemoveFailed);
            return;
        }
        await _trail.RecordAsync(ActivityEntity.KnownItem, item.Id, null, ActivityAction.KnownItemRemoved, ct);
        if (Editing?.Id == item.Id) CancelEdit();
        _caps.Remove(item.Id);
        Errors = [];
        Message = $"Removed \"{item.Text}\". Campaigns that used it keep their text.";
        await ReloadAsync(ct);
    }

    // ---- From the campaign editor ----

    /// <summary>
    /// Saves something written in a campaign as a known item, exactly as written. When it is already
    /// known, says so (the store's own refusal) rather than failing: the point was to have it in the
    /// list, and it is. When the store fails, says so (<see cref="CampaignSaveFailed"/>) and logs it,
    /// never throws: the campaign editor would lose her unsaved campaign with the connection.
    /// </summary>
    /// <returns>What to tell her.</returns>
    public static async Task<string> SaveFromCampaignAsync(
        IKnownItemStore store, ActivityTrail trail, KnownItem item, ILogger? log = null, CancellationToken ct = default)
    {
        try
        {
            await store.AddAsync(trail.Client, item, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            LogFailure(log ?? NullLogger.Instance, ex, "save from a campaign", item, trail.Client);
            return CampaignSaveFailed;
        }
        await trail.RecordAsync(ActivityEntity.KnownItem, item.Id, null, ActivityAction.KnownItemAdded, ct);
        return $"Saved \"{item.Text}\" to your known items.";
    }

    /// <summary>
    /// Adds the treatments a saved campaign's promotion lines named that are new to her list, each with
    /// its regular price (owner, 2026-11-01, P6: "It should be saved automatically"), through the store
    /// and the trail as every other add goes, one <see cref="ActivityAction.KnownItemAdded"/> event by
    /// id. Called once the campaign is saved, so the campaign is never held up by it. One already in her
    /// list by then (added in another tab) is refused by the store and passed over in silence: it is
    /// there, which was the point. A store failure is logged and said, never thrown.
    /// </summary>
    /// <returns>What to tell her, one sentence per treatment: "Added X to your treatments, regularly $5,100."</returns>
    public static async Task<IReadOnlyList<string>> AddFromPromotionAsync(
        IKnownItemStore store, ActivityTrail trail, IReadOnlyList<KnownTreatment> treatments, ILogger? log = null,
        CancellationToken ct = default)
    {
        var said = new List<string>();
        foreach (var treatment in treatments)
        {
            try
            {
                await store.AddAsync(trail.Client, treatment, ct);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                continue;
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                LogFailure(log ?? NullLogger.Instance, ex, "add from a promotion", treatment, trail.Client);
                said.Add($"Couldn't add {treatment.Name} to your treatments just now. Your campaign is saved; add it on the Known items page.");
                continue;
            }
            await trail.RecordAsync(ActivityEntity.KnownItem, treatment.Id, null, ActivityAction.KnownItemAdded, ct);
            said.Add(AddedSentence(treatment));
        }
        return said;
    }

    // Anything the store throws other than its own refusals (ArgumentException, InvalidOperationException,
    // which carry a message for her) and a cancelled request: Azure.RequestFailedException, a timeout,
    // a network or authentication failure, a row it cannot read back.
    private static bool IsStorageFailure(Exception ex) =>
        ex is not (ArgumentException or InvalidOperationException or OperationCanceledException);

    // By the item's id and kind and the client, never the item's text: the log carries no content.
    private static void LogFailure(ILogger log, Exception ex, string action, KnownItem item, ClientName client) =>
        log.LogError(ex, "Could not {Action} known item {ItemId} ({Kind}) for {Client}; she was told to try again.",
            action, item.Id, item.Kind, client.Value);

    // The list as stored. Reading it again after a change that went through is a courtesy: when it
    // fails the change still stands, the list shown is the one before it, and the failure is logged.
    private async Task ReloadAsync(CancellationToken ct)
    {
        try
        {
            Items = await _store.ForClientAsync(_trail.Client, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Could not read the known items of {Client} again after a change.", _trail.Client.Value);
        }
    }

    private bool Refuse(params string[] errors)
    {
        (Message, Errors) = (null, errors);
        return false;
    }

    private async Task<bool> ChangeAsync(KnownItem item, bool adding, CancellationToken ct)
    {
        if (!await WriteAsync(item, adding, ct)) return false;
        (Message, Errors) = ($"{(adding ? "Added" : "Changed")} \"{item.Text}\".", []);
        await ReloadAsync(ct);
        return true;
    }

    // One store call and its trail event; says nothing and reads nothing again, so a change of several
    // items can say it once.
    private async Task<bool> WriteAsync(KnownItem item, bool adding, CancellationToken ct)
    {
        try
        {
            if (adding) await _store.AddAsync(_trail.Client, item, ct);
            else await _store.UpdateAsync(_trail.Client, item, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Refuse(ex.Message);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            LogFailure(_log, ex, adding ? "add" : "change", item, _trail.Client);
            return Refuse(SaveFailed);
        }
        await _trail.RecordAsync(ActivityEntity.KnownItem, item.Id, null,
            adding ? ActivityAction.KnownItemAdded : ActivityAction.KnownItemChanged, ct);
        _caps.Remove(item.Id);
        return true;
    }

    // ---- A promotion and what it does to her treatments ----

    /// <summary>The new promotion's form.</summary>
    public KnownPromotionForm NewPromotion { get; private set; } = new();

    /// <summary>The promotion open for changing, or null.</summary>
    public KnownPromotionForm? EditPromotion { get; private set; }

    public async Task<bool> AddPromotionAsync(CancellationToken ct = default)
    {
        var (plan, errors) = NewPromotion.ToPlan(KnownItem.NewId(), Items);
        if (plan is null) return Refuse([.. errors]);
        if (!await ApplyAsync(plan, adding: true, ct)) return false;
        NewPromotion = new KnownPromotionForm();
        return true;
    }

    /// <summary>How far changing one of her treatments reaches, said beside its form; null when no saved promotion uses it.</summary>
    public string? Reach(KnownTreatment treatment) => Items.PromotionsUsing(treatment.Id).Count switch
    {
        0 => null,
        1 => "Used in 1 saved promotion; it will show the new price.",   // (proposed)
        var n => $"Used in {n} saved promotions; they will show the new price.",
    };

    // The promotion is checked against her list as the plan would leave it before anything is written,
    // so a promotion that would be refused adds no treatment and changes no price. Then her new
    // treatments, her repriced ones, and the promotion, in that order: a line never points at nothing.
    private async Task<bool> ApplyAsync(KnownPromotionPlan plan, bool adding, CancellationToken ct)
    {
        var provisional = new KnownItems([
            .. Items.All.Where(i => plan.Repriced.All(r => r.Id != i.Id)), .. plan.Repriced, .. plan.Added]);
        if (KnownItemRules.Problems(plan.Promotion, provisional) is { Count: > 0 } problems) return Refuse([.. problems]);

        var said = new List<string>();
        foreach (var treatment in plan.Added)
        {
            if (!await WriteAsync(treatment, adding: true, ct)) return await StoppedAsync(said, ct);
            said.Add(AddedSentence(treatment));
        }
        foreach (var treatment in plan.Repriced)
        {
            if (!await WriteAsync(treatment, adding: false, ct)) return await StoppedAsync(said, ct);
            said.Add($"Changed {treatment.Name}'s regular price to {Dollars.Text(treatment.RegularPrice!.Value)}.");   // (proposed)
        }
        if (!await WriteAsync(plan.Promotion, adding, ct)) return await StoppedAsync(said, ct);
        said.Add($"{(adding ? "Added" : "Changed")} \"{plan.Promotion.Text}\".");
        (Message, Errors) = (string.Join(' ', said), []);
        await ReloadAsync(ct);
        return true;
    }

    // A store call failed part way: what was written stands, so the list shown is read again and she is
    // told what joined or changed on it, beside the error. Her form keeps what she typed.
    private async Task<bool> StoppedAsync(List<string> written, CancellationToken ct)
    {
        if (written.Count == 0) return false;
        Message = string.Join(' ', written);
        await ReloadAsync(ct);
        return false;
    }

    private static string AddedSentence(KnownTreatment treatment) => treatment.RegularPrice is { } regular
        ? $"Added {treatment.Name} to your treatments, regularly {Dollars.Text(regular)}."
        : $"Added {treatment.Name} to your treatments.";
}
