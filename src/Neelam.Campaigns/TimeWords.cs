namespace Neelam.Campaigns;

/// <summary>Lengths of time as a page says them to a client.</summary>
public static class TimeWords
{
    private sealed record Unit(TimeSpan Length, string One, string Single, string Many);

    // Biggest first. No months or years (owner, 2026-11-03), so 365 days reads "365 days". Seconds only
    // below a minute, which the owner left to be chosen: the warm-up page's retry and the starts page's
    // thresholds are said in them.
    private static readonly Unit[] Units =
    [
        new(TimeSpan.FromDays(7), "a week", "week", "weeks"),
        new(TimeSpan.FromDays(1), "a day", "day", "days"),
        new(TimeSpan.FromHours(1), "an hour", "hour", "hours"),
        new(TimeSpan.FromMinutes(1), "a minute", "minute", "minutes"),
        new(TimeSpan.FromSeconds(1), "a second", "second", "seconds"),
    ];

    /// <summary>
    /// A period in words (owner, 2026-11-03), from the biggest unit it holds at least one whole of. When
    /// it is a whole number of that unit, it is said so: "2 weeks", "2 days", "a day", "an hour". When it
    /// is a whole number of the unit below, weeks to days or days to hours, it is said in that: "30 days",
    /// "36 hours", "365 days". Otherwise it is the biggest unit's wholes, rounded down, with a "+": "4
    /// weeks+" for 29.5 days, "57 weeks+" for 400.5 days, "1 day+" for 25.5 hours, "1 hour+" for 90
    /// minutes, "30 minutes+" for 30.5. Hours never go down to minutes, as the owner's "90 minutes" reads
    /// "1 hour+". Below a second, "less than a second".
    /// </summary>
    public static string Period(TimeSpan period)
    {
        var biggest = Array.FindIndex(Units, u => period >= u.Length);
        if (biggest < 0) return "less than a second";
        var unit = Units[biggest];
        if (period.Ticks % unit.Length.Ticks == 0) return Whole(period, unit);
        // Weeks to days, days to hours; hours and smaller are said in their own unit, with a "+".
        if (unit.Length >= TimeSpan.FromDays(1) && period.Ticks % Units[biggest + 1].Length.Ticks == 0)
            return Whole(period, Units[biggest + 1]);
        var wholes = period.Ticks / unit.Length.Ticks;
        return $"{wholes} {(wholes == 1 ? unit.Single : unit.Many)}+";
    }

    private static string Whole(TimeSpan period, Unit unit)
    {
        var wholes = period.Ticks / unit.Length.Ticks;
        return wholes == 1 ? unit.One : $"{wholes} {unit.Many}";
    }
}
