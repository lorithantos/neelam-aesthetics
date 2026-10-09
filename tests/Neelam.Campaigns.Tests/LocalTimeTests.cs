using System.Text.Json.Nodes;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Times are shown in the client's own time zone with its abbreviation, not in UTC: the walkthrough
/// read "Last saved 9 Oct 2026, 21:05 UTC" for five past two in the afternoon in Washington.
/// </summary>
public class LocalTimeTests
{
    private static readonly LocalTime Pacific = LocalTime.For(LocalTime.DefaultZone);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void A_time_is_shown_in_pacific_time_with_its_abbreviation() =>
        Assert.Equal("9 Oct 2026, 2:05 PM PDT", Pacific.DateAndTime(Utc(10, 9, 21, 5)));

    // Daylight time ends at 2:00 AM PDT on 1 November 2026 (09:00 UTC): the clock goes back to 1:00 AM PST.
    [Fact]
    public void Across_the_end_of_daylight_time_the_hour_and_the_abbreviation_change()
    {
        Assert.Equal("1 Nov 2026, 1:59 AM PDT", Pacific.DateAndTime(Utc(11, 1, 8, 59)));
        Assert.Equal("1 Nov 2026, 1:00 AM PST", Pacific.DateAndTime(Utc(11, 1, 9, 0)));
    }

    // Daylight time starts at 2:00 AM PST on 8 March 2026 (10:00 UTC): the clock goes on to 3:00 AM PDT.
    [Fact]
    public void Across_the_start_of_daylight_time_the_hour_and_the_abbreviation_change()
    {
        Assert.Equal("8 Mar 2026, 1:59 AM PST", Pacific.DateAndTime(Utc(3, 8, 9, 59)));
        Assert.Equal("8 Mar 2026, 3:00 AM PDT", Pacific.DateAndTime(Utc(3, 8, 10, 0)));
    }

    [Fact]
    public void A_save_s_time_of_day_has_its_seconds() =>
        Assert.Equal("2:05:09 PM PDT", Pacific.TimeOfDay(new DateTimeOffset(2026, 10, 9, 21, 5, 9, TimeSpan.Zero)));

    [Fact]
    public void The_setting_names_the_zone_and_blank_means_pacific()
    {
        Assert.Equal("9 Oct 2026, 5:05 PM EDT", LocalTime.For("America/New_York").DateAndTime(Utc(10, 9, 21, 5)));
        Assert.Equal("9 Oct 2026, 2:05 PM PDT", LocalTime.For(null).DateAndTime(Utc(10, 9, 21, 5)));
        Assert.Equal("9 Oct 2026, 2:05 PM PDT", LocalTime.For("  ").DateAndTime(Utc(10, 9, 21, 5)));
        Assert.Equal("9 Oct 2026, 9:05 PM UTC", LocalTime.For("Etc/UTC").DateAndTime(Utc(10, 9, 21, 5)));
        Assert.Throws<TimeZoneNotFoundException>(() => LocalTime.For("Mars/Olympus_Mons"));
    }

    // The app's own setting: Pacific, where the clinic is.
    [Fact]
    public void The_app_shows_pacific_time()
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web", "appsettings.json")))!;
        Assert.Equal("America/Los_Angeles", settings["Display"]!["TimeZone"]!.GetValue<string>());
    }
}
