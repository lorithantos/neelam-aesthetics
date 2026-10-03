using System.Text.RegularExpressions;

namespace Neelam.Campaigns.Storage;

/// <summary>
/// Whose saves these are, and the name of the container that holds them: one container per
/// client, so another business using this tool keeps its saves apart from this one's, and an app
/// identity granted one client's container cannot read another's. The shape is a container
/// name's: 3 to 63 lowercase letters, digits and single hyphens, starting and ending with a
/// letter or digit.
/// </summary>
public sealed partial record ClientName
{
    public string Value { get; }

    public ClientName(string value)
    {
        if (value is null || !Shape().IsMatch(value))
            throw new ArgumentException(
                $"'{value}' is not a client name: use 3-63 lowercase letters, digits and single hyphens, " +
                "starting and ending with a letter or digit.", nameof(value));
        Value = value;
    }

    public override string ToString() => Value;

    [GeneratedRegex("^(?=.{3,63}$)[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Shape();
}
