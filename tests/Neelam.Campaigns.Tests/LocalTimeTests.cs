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

    // The app's own default, for a client that registers no zone: Pacific, where the first clinic is
    // (owner, 2026-10-09). The setting is read by the name the code reads it by.
    [Fact]
    public void The_app_s_default_zone_is_pacific()
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(InfrastructureTests.Root, "src", "Neelam.Web", "appsettings.json")))!;
        Assert.Equal("Display:DefaultTimeZone", LocalTime.Setting);
        Assert.Equal("America/Los_Angeles", settings["Display"]!["DefaultTimeZone"]!.GetValue<string>());
    }

    // What a registered zone may be: one this machine knows, or none. The refusal names what was given.
    [Fact]
    public void A_zone_this_machine_does_not_know_is_a_problem_named_by_its_id()
    {
        Assert.Null(LocalTime.ProblemWith(null));
        Assert.Null(LocalTime.ProblemWith(" "));
        Assert.Null(LocalTime.ProblemWith("America/New_York"));
        Assert.Equal(
            "\"Mars/Olympus_Mons\" is not a time zone: give its IANA name, such as America/Los_Angeles or America/New_York.",
            LocalTime.ProblemWith(" Mars/Olympus_Mons "));
        Assert.Null(LocalTime.TryFor("Mars/Olympus_Mons"));
    }

    // A date alone is the day it was in the client's zone: 03:00 UTC on 10 October is still the 9th in Pacific time.
    [Fact]
    public void A_date_is_the_day_in_the_client_s_zone() =>
        Assert.Equal("9 Oct 2026", Pacific.Date(Utc(10, 10, 3, 0)));
}
