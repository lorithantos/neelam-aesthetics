using System.Globalization;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Periods in words (owner, 2026-11-03): every example the owner gave, and the seconds below a minute
/// the pages need, which the owner left to be chosen.
/// </summary>
public class TimeWordsTests
{
    private static string Words(string period) => TimeWords.Period(TimeSpan.Parse(period, CultureInfo.InvariantCulture));

    // A whole number of the biggest unit it holds, or of the unit below: weeks, days, hours, minutes.
    [Theory]
    [InlineData("14.00:00:00", "2 weeks")]
    [InlineData("30.00:00:00", "30 days")]
    [InlineData("2.00:00:00", "2 days")]
    [InlineData("1.12:00:00", "36 hours")]
    [InlineData("1.00:00:00", "a day")]
    [InlineData("7.00:00:00", "a week")]
    [InlineData("01:00:00", "an hour")]
    [InlineData("00:01:00", "a minute")]
    [InlineData("365.00:00:00", "365 days")]
    [InlineData("90.00:00:00", "90 days")]
    public void A_period_that_fits_a_unit_exactly_is_said_in_it(string period, string words) =>
        Assert.Equal(words, Words(period));

    // No exact fit: the biggest unit it holds a whole of, rounded down, with a "+"; one is "1", not "a".
    [Theory]
    [InlineData("29.12:00:00", "4 weeks+")]
    [InlineData("1.01:30:00", "1 day+")]
    [InlineData("01:30:00", "1 hour+")]
    [InlineData("00:30:30", "30 minutes+")]
    [InlineData("400.12:00:00", "57 weeks+")]
    public void A_period_that_fits_no_unit_is_its_wholes_rounded_down_and_more(string period, string words) =>
        Assert.Equal(words, Words(period));

    // Below a minute, the choice made here and the owner's to overrule: seconds, the same way.
    [Theory]
    [InlineData("00:00:30", "30 seconds")]
    [InlineData("00:00:01", "a second")]
    [InlineData("00:00:01.5000000", "1 second+")]
    [InlineData("00:00:00.5000000", "less than a second")]
    [InlineData("00:00:00", "less than a second")]
    public void Below_a_minute_it_is_seconds_and_below_a_second_less_than_one(string period, string words) =>
        Assert.Equal(words, Words(period));
}
