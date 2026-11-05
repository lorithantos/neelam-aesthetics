namespace Neelam.Campaigns.Storage;

/// <summary>
/// What an open editor tab is editing, for the notice that the same thing is also being edited in another
/// tab: one campaign or template by its id, or the client's look, of which she has one. A kind and an id
/// only, never content, so it can name a table row (partition <c>{client}|{kind}</c> or
/// <c>{client}|{kind}|{id:N}</c>).
/// </summary>
public sealed record TabSubject
{
    private TabSubject(string kind, Guid? id)
    {
        Kind = kind;
        Id = id;
    }

    /// <summary><c>campaign</c>, <c>template</c> or <c>look</c>.</summary>
    public string Kind { get; }

    /// <summary>The campaign's or template's id; null for the look.</summary>
    public Guid? Id { get; }

    public static TabSubject Campaign(Guid id) => new("campaign", id);

    public static TabSubject Template(Guid id) => new("template", id);

    /// <summary>The client's look.</summary>
    public static TabSubject Look { get; } = new("look", null);

    public override string ToString() => Id is { } id ? $"{Kind}/{id:N}" : Kind;
}
