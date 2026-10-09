using Neelam.Campaigns;
using Neelam.Campaigns.Storage;

namespace Neelam.Web.Components.Pages;

/// <summary>
/// A client as the Clients page edits it: plain text fields, turned into a <see cref="ClientRecord"/>
/// only when every one makes sense, with the reasons when they do not.
/// </summary>
public sealed class ClientForm
{
    public string Name { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>The numbers the business may publish, one per line, written any usual way.</summary>
    public string Phones { get; set; } = "";

    /// <summary>The time zone's IANA id, such as America/New_York; blank for the default, Pacific.</summary>
    public string TimeZone { get; set; } = "";

    public static ClientForm Of(ClientRecord client) => new()
    {
        Name = client.Name.Value,
        GroupId = client.GroupId.ToString(),
        DisplayName = client.DisplayName,
        Description = client.Description ?? "",
        Phones = string.Join("\n", client.Phones.Select(p => p.Formatted)),
        TimeZone = client.TimeZone ?? "",
    };

    /// <returns>The record, or null with what is wrong.</returns>
    public (ClientRecord? Client, IReadOnlyList<string> Errors) ToRecord()
    {
        var errors = new List<string>();

        ClientName? name = null;
        try
        {
            name = new ClientName(Name.Trim());
        }
        catch (ArgumentException ex)
        {
            errors.Add(ex.Message.Split(" (Parameter")[0]);
        }

        if (!Guid.TryParse(GroupId.Trim(), out var group) || group == Guid.Empty)
            errors.Add("The Entra group ID is the group's Object ID, a GUID such as 0f6e1c3a-....");
        if (string.IsNullOrWhiteSpace(DisplayName))
            errors.Add("Give the client a display name, the one its people see.");

        var phones = new List<PhoneNumber>();
        foreach (var line in Phones.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (PhoneNumber.TryParse(line, out var phone)) phones.Add(phone);
            else errors.Add($"\"{line}\" is not a phone number: give the area code, such as (425) 877-8646, or + and the country code.");
        }

        var zone = TimeZone.Trim();
        if (LocalTime.ProblemWith(zone) is { } zoneProblem) errors.Add(zoneProblem);

        var description = Description.Replace("\r\n", "\n").Trim();
        return errors.Count == 0
            ? (new ClientRecord(name!, group, DisplayName.Trim(), description.Length > 0 ? description : null)
                { Phones = new PhoneNumbers(phones), TimeZone = zone.Length > 0 ? zone : null }, [])
            : (null, errors);
    }
}
