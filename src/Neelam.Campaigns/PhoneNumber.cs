using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Neelam.Campaigns;

/// <summary>
/// A phone number, kept as its digits with the country code: ten digits are a North American
/// number, so +1 is assumed for them. "425.877.8646", "(425) 877-8646" and "tel:+14258778646" are
/// all the number 14258778646, and it is shown as "(425) 877-8646".
/// </summary>
public sealed partial record PhoneNumber
{
    private PhoneNumber(string digits) => Digits = digits;

    /// <summary>The country code and the number, digits only, e.g. "14258778646".</summary>
    public string Digits { get; }

    /// <summary>"(425) 877-8646" for a North American number, "+" and the digits for any other.</summary>
    public string Formatted =>
        Digits.Length == 11 && Digits[0] == '1'
            ? $"({Digits[1..4]}) {Digits[4..7]}-{Digits[7..]}"
            : $"+{Digits}";

    public override string ToString() => Formatted;

    /// <summary>
    /// A number as a person writes it: digits with spaces, dots, hyphens or brackets, optionally a
    /// leading "+" or "tel:". Ten digits are a North American number; eleven starting with 1 are one
    /// with its country code; any other length needs the "+" and its country code.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PhoneNumber? number)
    {
        number = null;
        var clean = (text ?? "").Trim();
        if (clean.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) clean = clean[4..].Trim();
        var international = clean.StartsWith('+');
        if (international) clean = clean[1..];
        if (clean.Length == 0 || clean.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '.' or '-' or '(' or ')')))
            return false;

        var digits = new string(clean.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 10 && !international) digits = "1" + digits;
        else if (!(digits.Length == 11 && digits[0] == '1') && !(international && digits.Length is >= 8 and <= 15))
            return false;
        number = new PhoneNumber(digits);
        return true;
    }

    /// <summary>A number as <see cref="Digits"/> stores it; null for anything else.</summary>
    public static PhoneNumber? FromDigits(string digits) =>
        digits.Length is >= 8 and <= 15 && digits.All(char.IsAsciiDigit) ? new PhoneNumber(digits) : null;

    /// <summary>
    /// Every North American number written in a piece of text, as written, in the forms emails use:
    /// 425-877-8646, (425) 773-5261, 425.877.8646, +1 425 877 8646, and tel:+14258778646 in a link.
    /// </summary>
    public static IEnumerable<(string Written, PhoneNumber Number)> FindIn(string text)
    {
        foreach (Match m in Written().Matches(text))
            if (TryParse(m.Value, out var number))
                yield return (m.Value, number);
    }

    // An optional +1 or 1, then the area code (bracketed or not), the exchange and the line, each
    // optionally separated by a space, dot or hyphen; never part of a longer run of digits.
    [GeneratedRegex(@"(?<!\d)(?:\+?1[ .-]?)?(?:\(\d{3}\) ?|\d{3}[ .-]?)\d{3}[ .-]?\d{4}(?!\d)")]
    private static partial Regex Written();
}

/// <summary>
/// The phone numbers a business may publish, in the order they were registered, each once. Equal
/// when they hold the same numbers in the same order, so a record holding them compares by value.
/// </summary>
public sealed class PhoneNumbers : IReadOnlyList<PhoneNumber>, IEquatable<PhoneNumbers>
{
    private readonly PhoneNumber[] _numbers;

    public PhoneNumbers(IEnumerable<PhoneNumber> numbers) => _numbers = numbers.Distinct().ToArray();

    public static PhoneNumbers None { get; } = new([]);

    public int Count => _numbers.Length;

    public PhoneNumber this[int index] => _numbers[index];

    public bool Contains(PhoneNumber number) => _numbers.Contains(number);

    public IEnumerator<PhoneNumber> GetEnumerator() => ((IEnumerable<PhoneNumber>)_numbers).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(PhoneNumbers? other) => other is not null && _numbers.SequenceEqual(other._numbers);

    public override bool Equals(object? obj) => Equals(obj as PhoneNumbers);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var n in _numbers) hash.Add(n);
        return hash.ToHashCode();
    }

    /// <summary>The numbers as shown, e.g. "(425) 877-8646, (425) 773-5261".</summary>
    public override string ToString() => string.Join(", ", _numbers.Select(n => n.Formatted));
}
