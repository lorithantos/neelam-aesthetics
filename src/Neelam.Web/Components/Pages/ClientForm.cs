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

    public static ClientForm Of(ClientRecord client) => new()
    {
        Name = client.Name.Value,
        GroupId = client.GroupId.ToString(),
        DisplayName = client.DisplayName,
        Description = client.Description ?? "",
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

        var description = Description.Replace("\r\n", "\n").Trim();
        return errors.Count == 0
            ? (new ClientRecord(name!, group, DisplayName.Trim(), description.Length > 0 ? description : null), [])
            : (null, errors);
    }
}
