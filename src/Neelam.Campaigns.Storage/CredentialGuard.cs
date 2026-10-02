namespace Neelam.Campaigns.Storage;

/// <summary>
/// Refuses to start the app if any stored credential is present in its configuration: a
/// connection string, an account key, a SAS token, a password. Access is by managed identity
/// only, so any of these is either a mistake or a leak. Only setting names are reported, never values.
/// </summary>
public static class CredentialGuard
{
    private static readonly string[] SecretMarkers =
    [
        "AccountKey=",
        "SharedAccessKey=",
        "SharedAccessSignature=",
        "Password=",
        "InstrumentationKey=",
    ];

    /// <param name="settings">Typically <c>IConfiguration.AsEnumerable()</c>.</param>
    public static void EnsureNoStoredCredentials(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        var offending = settings
            .Where(kv => !string.IsNullOrEmpty(kv.Value) && LooksLikeCredential(kv.Key, kv.Value!))
            .Select(kv => kv.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (offending.Count > 0)
            throw new InvalidOperationException(
                "Stored credentials are not allowed; this app uses managed identity only. Remove: " +
                string.Join(", ", offending));
    }

    private static bool LooksLikeCredential(string key, string value) =>
        key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)
        || SecretMarkers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase))
        || IsSasUrl(value);

    private static bool IsSasUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Query.Contains("sig=", StringComparison.OrdinalIgnoreCase);
}
