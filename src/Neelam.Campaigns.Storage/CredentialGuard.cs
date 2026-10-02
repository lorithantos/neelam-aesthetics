namespace Neelam.Campaigns.Storage;

/// <summary>
/// Refuses to start the app if a stored secret is present in its configuration: an account key,
/// a SAS token, a password, a client secret. Access is by managed identity only, so any of these is
/// either a mistake or a leak. Only setting names are reported, never values.
/// A connection string carrying no secret is allowed: Application Insights is addressed by one even
/// when its ingestion is Entra-only. What keeps a connection string safe is where it is stored, an
/// App Service setting and never a file in the repository; <c>RepositoryTests</c> pins that half.
/// </summary>
public static class CredentialGuard
{
    private static readonly string[] SecretMarkers =
    [
        "AccountKey=",
        "SharedAccessKey=",
        "SharedAccessSignature=",
        "Password=",
        "Secret=",
    ];

    /// <param name="settings">Typically <c>IConfiguration.AsEnumerable()</c>.</param>
    public static void EnsureNoStoredCredentials(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        var offending = settings
            .Where(kv => !string.IsNullOrEmpty(kv.Value) && LooksLikeCredential(kv.Value!))
            .Select(kv => kv.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (offending.Count > 0)
            throw new InvalidOperationException(
                "Stored credentials are not allowed; this app uses managed identity only. Remove: " +
                string.Join(", ", offending));
    }

    private static bool LooksLikeCredential(string value) =>
        SecretMarkers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase))
        || IsSasUrl(value);

    private static bool IsSasUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Query.Contains("sig=", StringComparison.OrdinalIgnoreCase);
}
