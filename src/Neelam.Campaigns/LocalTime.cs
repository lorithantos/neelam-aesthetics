using System.Globalization;

namespace Neelam.Campaigns;

/// <summary>
/// Times as the client reads them: in her own time zone, with its abbreviation, such as
/// "9 Oct 2026, 2:05 PM PDT". Every page that shows a time goes through this one helper; times are
/// still kept in UTC everywhere else (blob names, metadata, tables). The zone is the
/// <c>Display:TimeZone</c> setting, an IANA id; <see cref="DefaultZone"/> when it is not set, since
/// the first client is in Washington.
/// </summary>
public sealed class LocalTime
{
    /// <summary>The configuration key the zone is read from.</summary>
    public const string Setting = "Display:TimeZone";

    /// <summary>The zone when none is configured: Pacific time, where Neelam Aesthetics is.</summary>
    public const string DefaultZone = "America/Los_Angeles";

    public LocalTime(TimeZoneInfo zone) => Zone = zone;

    public TimeZoneInfo Zone { get; }

    /// <summary>The zone named by the setting, or <see cref="DefaultZone"/> when it is blank.</summary>
    /// <exception cref="TimeZoneNotFoundException">The setting names no zone this machine knows, so the app refuses to start.</exception>
    public static LocalTime For(string? zoneId) =>
        new(TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(zoneId) ? DefaultZone : zoneId.Trim()));

    /// <summary>A date and time, such as "9 Oct 2026, 2:05 PM PDT".</summary>
    public string DateAndTime(DateTimeOffset at) => $"{Local(at).ToString("d MMM yyyy, h:mm tt", CultureInfo.InvariantCulture)} {Abbreviation(at)}";

    /// <summary>A time of day to the second, such as "2:05:09 PM PDT", for a save just made.</summary>
    public string TimeOfDay(DateTimeOffset at) => $"{Local(at).ToString("h:mm:ss tt", CultureInfo.InvariantCulture)} {Abbreviation(at)}";

    /// <summary>
    /// The zone's abbreviation at that moment, standard or daylight: "PST" or "PDT". Taken from the
    /// initials of the zone's name ("Pacific Daylight Time"); where the system gives no such name,
    /// the offset from UTC, such as "UTC-7".
    /// </summary>
    public string Abbreviation(DateTimeOffset at)
    {
        if (Zone == TimeZoneInfo.Utc || Zone.Id is "UTC" or "Etc/UTC") return "UTC";
        var name = Zone.IsDaylightSavingTime(at) ? Zone.DaylightName : Zone.StandardName;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words[^1] == "Time" && words.All(w => char.IsAsciiLetterUpper(w[0])))
            return string.Concat(words.Select(w => w[0]));
        var offset = Zone.GetUtcOffset(at);
        return offset.Minutes == 0
            ? $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours)}"
            : $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours)}:{Math.Abs(offset.Minutes):00}";
    }

    private DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone).DateTime;
}
