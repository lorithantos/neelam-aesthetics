using System.Text.RegularExpressions;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// A client of this deployment, and the name of the container that holds that client's own data:
/// one container per client, laid out the same way for each. Which client a request may reach is
/// decided in code from who is signed in, not here. The shape is a container name's: 3 to 63
/// lowercase letters, digits and single hyphens, starting and ending with a letter or digit, and
/// never the name of a shared container such as <c>settings</c>.
/// </summary>
public sealed partial record ClientName
{
    public string Value { get; }

    /// <summary>Containers that hold no client's data, so no client may be named after them.</summary>
    public static readonly IReadOnlyList<string> Reserved = [ClientStores.SettingsContainer];

    public ClientName(string value)
    {
        if (value is null || !Shape().IsMatch(value))
            throw new ArgumentException(
                $"'{value}' is not a client name: use 3-63 lowercase letters, digits and single hyphens, " +
                "starting and ending with a letter or digit.", nameof(value));
        if (Reserved.Contains(value))
            throw new ArgumentException($"'{value}' is reserved for a shared container, not a client.", nameof(value));
        Value = value;
    }

    public override string ToString() => Value;

    [GeneratedRegex("^(?=.{3,63}$)[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Shape();
}
