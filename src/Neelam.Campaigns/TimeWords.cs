namespace Neelam.Campaigns;

/// <summary>Lengths of time as a page says them to a client.</summary>
public static class TimeWords
{
    /// <summary>
    /// A period in words, such as "a day", "2 days", "12 hours", "30 minutes" or "a second": the
    /// largest whole unit it is a number of, so a configured grace period or a threshold in code reads
    /// as it was meant.
    /// </summary>
    public static string Period(TimeSpan period)
    {
        if (period >= TimeSpan.FromDays(1) && period.Ticks % TimeSpan.TicksPerDay == 0)
            return Count((int)period.TotalDays, "a day", "days");
        if (period >= TimeSpan.FromHours(1) && period.Ticks % TimeSpan.TicksPerHour == 0)
            return Count((int)period.TotalHours, "an hour", "hours");
        if (period >= TimeSpan.FromMinutes(1) && period.Ticks % TimeSpan.TicksPerMinute == 0)
            return Count((int)period.TotalMinutes, "a minute", "minutes");
        if (period >= TimeSpan.FromSeconds(1) && period < TimeSpan.FromMinutes(1) && period.Ticks % TimeSpan.TicksPerSecond == 0)
            return Count((int)period.TotalSeconds, "a second", "seconds");
        return period.TotalHours >= 1 ? $"{period.TotalHours:0.#} hours" : $"{period.TotalMinutes:0.#} minutes";
    }

    private static string Count(int n, string one, string many) => n == 1 ? one : $"{n} {many}";
}
