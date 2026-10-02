namespace Neelam.Campaigns;

/// <summary>Where a draft value came from. This is what lets a copy be told apart from a decision.</summary>
public enum Origin
{
    /// <summary>Nothing yet.</summary>
    Empty,

    /// <summary>Fixed text supplied by the template (sign-off, disclaimer). Ready as is.</summary>
    Template,

    /// <summary>Copied from somewhere else. Not ready until a person edits or confirms it.</summary>
    Copied,

    /// <summary>Typed or confirmed by a person.</summary>
    Entered,
}

/// <summary>One editable value in a draft, remembering where it came from.</summary>
public sealed class Slot<T>
{
    private T _value = default!;

    public Origin Origin { get; private set; } = Origin.Empty;

    /// <summary>Where a copied value came from, e.g. "Tier 1", for the message that asks to review it.</summary>
    public string? CopiedFrom { get; private set; }

    public bool HasValue => Origin != Origin.Empty;

    /// <summary>Ready to build: has a value, and if it was copied, a person has looked at it.</summary>
    public bool IsReady => Origin is Origin.Template or Origin.Entered;

    public T Value => HasValue ? _value : throw new InvalidOperationException("Slot is empty.");

    public static Slot<T> Empty() => new();

    public static Slot<T> FromTemplate(T value) => new() { _value = value, Origin = Origin.Template };

    public static Slot<T> CopiedFromSource(T value, string source) =>
        new() { _value = value, Origin = Origin.Copied, CopiedFrom = source };

    /// <summary>A person typed this value.</summary>
    public void Set(T value)
    {
        _value = value;
        Origin = Origin.Entered;
        CopiedFrom = null;
    }

    /// <summary>A person looked at a copied value and decided it is right as it stands.</summary>
    public void Confirm()
    {
        if (Origin != Origin.Copied)
            throw new InvalidOperationException("Only a copied value needs confirming.");
        Origin = Origin.Entered;
    }

    public void Clear()
    {
        _value = default!;
        Origin = Origin.Empty;
        CopiedFrom = null;
    }
}
