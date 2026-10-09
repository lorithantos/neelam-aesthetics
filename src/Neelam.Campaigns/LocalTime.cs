using System.Globalization;

namespace Neelam.Campaigns;

/// <summary>
/// Times as the client reads them: in her own time zone, with its abbreviation, such as
/// "9 Oct 2026, 2:05 PM PDT". Every page that shows a time goes through this one helper; times are
/// still kept in UTC everywhere else (blob names, metadata, tables). The zone is the client's own,
/// registered in the clients table (<c>ClientRecord.TimeZone</c>, an IANA id); for a client with
/// none, the deployment's default, the <c>Display:DefaultTimeZone</c> setting, which is
/// <see cref="DefaultZone"/> when not set (owner, 2026-10-09: "Pacific is fine as a default, but the
/// setting should be in the client metadata").
/// </summary>
public sealed class LocalTime
{
    /// <summary>The configuration key the default zone is read from: the zone of a client that registers none.</summary>
    public const string Setting = "Display:DefaultTimeZone";

    /// <summary>The default zone when none is configured: Pacific time, where Neelam Aesthetics is.</summary>
    public const string DefaultZone = "America/Los_Angeles";

    public LocalTime(TimeZoneInfo zone) => Zone = zone;

    public TimeZoneInfo Zone { get; }

    /// <summary>The zone named by the setting, or <see cref="DefaultZone"/> when it is blank.</summary>
    /// <exception cref="TimeZoneNotFoundException">The setting names no zone this machine knows, so the app refuses to start.</exception>
    public static LocalTime For(string? zoneId) =>
        new(TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(zoneId) ? DefaultZone : zoneId.Trim()));

    /// <summary>The zone with this id, or null when this machine knows no zone by it.</summary>
    public static LocalTime? TryFor(string zoneId) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(zoneId.Trim(), out var zone) ? new LocalTime(zone) : null;

    /// <summary>
    /// What is wrong with a zone id a client is registered with, naming it; null when it is a zone this
    /// machine knows, or blank (the default).
    /// </summary>
    public static string? ProblemWith(string? zoneId) =>
        string.IsNullOrWhiteSpace(zoneId) || TryFor(zoneId) is not null
            ? null
            : $"\"{zoneId.Trim()}\" is not a time zone: give its IANA name, such as America/Los_Angeles or America/New_York.";

    /// <summary>A date and time, such as "9 Oct 2026, 2:05 PM PDT".</summary>
    public string DateAndTime(DateTimeOffset at) => $"{Local(at).ToString("d MMM yyyy, h:mm tt", CultureInfo.InvariantCulture)} {Abbreviation(at)}";

    /// <summary>The date alone, such as "9 Oct 2026": the day it was in the client's zone.</summary>
    public string Date(DateTimeOffset at) => Local(at).ToString("d MMM yyyy", CultureInfo.InvariantCulture);

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

/// <summary>
/// The deployment's default zone (<see cref="LocalTime.Setting"/>), for a client that registers none.
/// Its own type, so a page cannot ask for "the" zone by mistake: pages get a client's times from the
/// client's workspace.
/// </summary>
public sealed record DefaultTimeZone(LocalTime Times);
