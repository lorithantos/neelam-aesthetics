using Azure;
using Azure.Identity;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// Storage could not be reached, or would not answer, while a page acted for her: Azure's own failure
/// (<see cref="RequestFailedException"/>, a timeout or a 503 among them), the network's
/// (<see cref="HttpRequestException"/>), or the site's identity not getting a token
/// (<see cref="AuthenticationFailedException"/>). On an interactive page an exception that escapes a
/// click ends the connection, and her unsaved work with it (2026-11-03), so the editors catch these, and
/// only these, and say <see cref="NotDone"/>. Anything else is a fault in the app and is left to surface.
/// The stores' own refusals (<see cref="InvalidOperationException"/>, <see cref="ArgumentException"/>,
/// <see cref="CampaignChangedException"/>) carry words for her and are never counted here.
/// </summary>
public static class StorageFailed
{
    /// <summary>Whether <paramref name="ex"/> is storage failing, rather than the app or her input.</summary>
    public static bool Is(Exception ex) =>
        ex is RequestFailedException or HttpRequestException or AuthenticationFailedException;

    /// <summary>
    /// What a page says when storage failed under one of her actions: that it did not happen, and that
    /// the page is as she left it. <paramref name="notDone"/> is what did not happen, such as "Not saved".
    /// Proposed wording, the owner's to change.
    /// </summary>
    public static string NotDone(string notDone) =>
        $"{notDone}: your saved work couldn't be reached just now. Everything on this page is as you left it, so try again in a moment.";
}
