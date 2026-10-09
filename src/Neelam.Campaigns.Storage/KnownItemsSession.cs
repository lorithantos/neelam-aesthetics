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
    private readonly IKnownItemStore _store;
    private readonly ActivityTrail _trail;

    private KnownItemsSession(IKnownItemStore store, ActivityTrail trail, KnownItems items)
    {
        _store = store;
        _trail = trail;
        Items = items;
    }

    public static async Task<KnownItemsSession> OpenAsync(IKnownItemStore store, ActivityTrail trail, CancellationToken ct = default) =>
        new(store, trail, await store.ForClientAsync(trail.Client, ct));

    public KnownItems Items { get; private set; }

    /// <summary>What the last action did, for a status line.</summary>
    public string? Message { get; private set; }

    /// <summary>Why the last action could not be done; empty when it was.</summary>
    public IReadOnlyList<string> Errors { get; private set; } = [];

    // ---- Adding ----

    public string NewTreatment { get; set; } = "";

    public BenefitEditor NewBenefit { get; private set; } = BenefitEditor.Standalone();

    public KnownTierForm NewTier { get; private set; } = new();

    public async Task<bool> AddTreatmentAsync(CancellationToken ct = default)
    {
        if (!await ChangeAsync(new KnownTreatment(KnownItem.NewId(), NewTreatment.Trim()), adding: true, ct)) return false;
        NewTreatment = "";
        return true;
    }

    public async Task<bool> AddBenefitAsync(CancellationToken ct = default)
    {
        if (NewBenefit.ToKnown() is not { } benefit)
            return Refuse(NewBenefit.Error ?? "Choose what kind of benefit, and fill it in.");
        if (!await ChangeAsync(benefit, adding: true, ct)) return false;
        NewBenefit = BenefitEditor.Standalone();
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

    public string EditTreatment { get; set; } = "";
    public BenefitEditor? EditBenefit { get; private set; }
    public KnownTierForm? EditTier { get; private set; }

    public void StartEdit(KnownItem item)
    {
        Editing = item;
        (Message, Errors) = (null, []);
        EditTreatment = item is KnownTreatment t ? t.Name : "";
        EditBenefit = item is KnownBenefit b ? BenefitEditor.Standalone(b.Benefit) : null;
        EditTier = item is KnownTier tier ? KnownTierForm.Of(tier) : null;
    }

    public void CancelEdit()
    {
        Editing = null;
        (EditBenefit, EditTier, EditTreatment) = (null, null, "");
        Errors = [];
    }

    public async Task<bool> SaveEditAsync(CancellationToken ct = default)
    {
        if (Editing is not { } editing) return false;
        KnownItem? changed;
        switch (editing)
        {
            case KnownTreatment:
                changed = new KnownTreatment(editing.Id, EditTreatment.Trim());
                break;
            case KnownBenefit:
                if (EditBenefit!.Value is not { } benefit)
                    return Refuse(EditBenefit.Error ?? "Fill in the benefit.");
                changed = new KnownBenefit(editing.Id, benefit);
                break;
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

    // ---- Removing ----

    public async Task RemoveAsync(KnownItem item, CancellationToken ct = default)
    {
        await _store.RemoveAsync(_trail.Client, item.Id, ct);
        await _trail.RecordAsync(ActivityEntity.KnownItem, item.Id, null, ActivityAction.KnownItemRemoved, ct);
        if (Editing?.Id == item.Id) CancelEdit();
        Errors = [];
        Message = $"Removed \"{item.Text}\". Campaigns that used it keep their text.";
        Items = await _store.ForClientAsync(_trail.Client, ct);
    }

    // ---- From the campaign editor ----

    /// <summary>
    /// Saves something written in a campaign as a known item, exactly as written. When it is already
    /// known, says so (the store's own refusal) rather than failing: the point was to have it in the
    /// list, and it is.
    /// </summary>
    /// <returns>What to tell her.</returns>
    public static async Task<string> SaveFromCampaignAsync(
        IKnownItemStore store, ActivityTrail trail, KnownItem item, CancellationToken ct = default)
    {
        try
        {
            await store.AddAsync(trail.Client, item, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }
        await trail.RecordAsync(ActivityEntity.KnownItem, item.Id, null, ActivityAction.KnownItemAdded, ct);
        return $"Saved \"{item.Text}\" to your known items.";
    }

    private bool Refuse(params string[] errors)
    {
        (Message, Errors) = (null, errors);
        return false;
    }

    private async Task<bool> ChangeAsync(KnownItem item, bool adding, CancellationToken ct)
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
        await _trail.RecordAsync(ActivityEntity.KnownItem, item.Id, null,
            adding ? ActivityAction.KnownItemAdded : ActivityAction.KnownItemChanged, ct);
        (Message, Errors) = ($"{(adding ? "Added" : "Changed")} \"{item.Text}\".", []);
        Items = await _store.ForClientAsync(_trail.Client, ct);
        return true;
    }
}
