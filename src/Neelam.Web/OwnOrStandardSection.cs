using Microsoft.AspNetCore.Components;
using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Web;

/// <summary>
/// A section showing a document a client may keep of her own in place of the standard (the template
/// baseline and the tier-name ladders, <see cref="ClientStores"/>), which another tab or person may change
/// while it is open (2026-10-11, phase 2). It reads what is in force for the client the access check gives,
/// and watches the version it showed. Each save is made only while her newest version is still the one
/// shown; when it is not, the save is refused and offered again as Keep mine, beside Load the latest.
/// One copy since 2026-11-03; the two sections each had their own.
/// </summary>
public abstract class OwnOrStandardSection : WatchedComponent<DocumentVersion>
{
    [Inject]
    protected ClientWorkspace Workspace { get; set; } = default!;

    [Inject]
    protected ClientStores Stores { get; set; } = default!;

    /// <summary>The client whose document it is; null when the caller can reach none, and then the section shows nothing.</summary>
    protected ClientName? Client { get; private set; }

    /// <summary>What the section says after an action; null for nothing.</summary>
    protected string? Message { get; set; }

    /// <summary>Her document as this section shows it, which the section's look at storage watches.</summary>
    protected HeldDocument? Held { get; private set; }

    /// <summary>A save refused because her document changed since it was shown; null while none is.</summary>
    protected RefusedSave? Refused { get; private set; }

    /// <summary>A refused save: what was said, and the same save again over the other version, for Keep mine.</summary>
    protected sealed record RefusedSave(string Message, Func<Task<string>> KeepMine);

    /// <summary>
    /// Reads what is in force for <paramref name="client"/>, puts it in the section with her working copy,
    /// and gives her document as the section now holds it.
    /// </summary>
    protected abstract Task<HeldDocument> ReadAsync(ClientName client);

    protected override async Task OnInitializedAsync()
    {
        (Client, _) = await Workspace.ClientDataAsync();
        if (Client is null) return;
        await LoadAsync();
        Watch(Held);
    }

    private async Task LoadAsync()
    {
        Held = await ReadAsync(Client!);
        Refused = null;
    }

    /// <summary>
    /// One save, given whether she keeps hers and saying what it did: refused when her document changed
    /// since it was shown, and then offered again as Keep mine. Done, the section reads her document again,
    /// and watches what it holds now; what storage says meanwhile is not news.
    /// </summary>
    protected Task SavingAsync(Func<bool, Task<string>> save) => ActingAsync(async () =>
    {
        try
        {
            Message = await save(false);
            await LoadAsync();
        }
        catch (CampaignChangedException ex)
        {
            Message = null;
            Refused = new RefusedSave(ex.Message, () => save(true));
        }
        finally
        {
            Watch(Held);
        }
    });

    /// <summary>"Keep mine": the refused save, made over the other version, which stays stored.</summary>
    protected async Task KeepMineAsync()
    {
        if (Refused is not { } refused) return;
        await SavingAsync(_ => refused.KeepMine());
    }

    /// <summary>"Load the latest": her document as storage has it now, replacing what she did here. Only ever on her click.</summary>
    protected Task LoadLatestAsync() => ActingAsync(async () =>
    {
        try
        {
            await LoadAsync();
            Message = "The latest version is open.";
        }
        finally
        {
            Watch(Held);
        }
    });
}
