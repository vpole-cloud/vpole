using Pole.Features.Functions.Domain;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionCronScheduleTests
{
    [Theory]
    [InlineData("* * * * *")]
    [InlineData("0,15,30,45 8-17/3 1,15 * 1-5")]
    [InlineData("5/10 0 29 2 0,7")]
    [InlineData("*/5 */2 1-31/3 1-12/2 0-7")]
    public void Parse_accepts_supported_five_field_syntax(string expression)
    {
        var schedule = FunctionCronSchedule.Parse(expression);

        Assert.Equal(expression, schedule.Expression);
        Assert.True(FunctionCronSchedule.TryParse(expression, out var parsed));
        Assert.NotNull(parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * 32 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("*/0 * * * *")]
    [InlineData("*/61 * * * *")]
    [InlineData("20-10 * * * *")]
    [InlineData("1,,2 * * * *")]
    [InlineData("1/2/3 * * * *")]
    [InlineData("+1 * * * *")]
    [InlineData("one * * * *")]
    public void Parse_rejects_invalid_syntax_and_ranges(string expression)
    {
        Assert.Throws<FormatException>(() => FunctionCronSchedule.Parse(expression));
        Assert.False(FunctionCronSchedule.TryParse(expression, out var schedule));
        Assert.Null(schedule);
    }

    [Fact]
    public void GetNextOccurrence_is_strictly_after_the_input_minute()
    {
        var schedule = FunctionCronSchedule.Parse("*/15 * * * *");

        Assert.Equal(
            Utc("2026-07-18T10:15:00Z"),
            schedule.GetNextOccurrence(Utc("2026-07-18T10:00:00Z")));
        Assert.Equal(
            Utc("2026-07-18T10:15:00Z"),
            schedule.GetNextOccurrence(Utc("2026-07-18T10:14:59Z")));
    }

    [Fact]
    public void GetNextOccurrence_supports_lists_ranges_and_steps()
    {
        var schedule = FunctionCronSchedule.Parse("10,40 8-14/3 * 1,7 1-5");

        Assert.Equal(
            Utc("2026-07-20T08:10:00Z"),
            schedule.GetNextOccurrence(Utc("2026-07-18T09:00:00Z")));
        Assert.Equal(
            Utc("2026-07-20T11:10:00Z"),
            schedule.GetNextOccurrence(Utc("2026-07-20T08:40:00Z")));
    }

    [Fact]
    public void Restricted_day_of_month_and_day_of_week_use_or_semantics()
    {
        var schedule = FunctionCronSchedule.Parse("0 9 13 * 1");

        Assert.Equal(
            Utc("2026-07-20T09:00:00Z"),
            schedule.GetNextOccurrence(Utc("2026-07-18T00:00:00Z")));
        Assert.Equal(
            Utc("2026-07-27T09:00:00Z"),
            schedule.GetNextOccurrence(Utc("2026-07-20T09:00:00Z")));
        Assert.True(schedule.IsMatch(Utc("2026-08-13T09:00:00Z")));
    }

    [Fact]
    public void Unrestricted_day_field_does_not_force_or_semantics()
    {
        var mondays = FunctionCronSchedule.Parse("0 9 * * 1");
        var thirteenths = FunctionCronSchedule.Parse("0 9 13 * *");

        Assert.Equal(
            Utc("2026-07-20T09:00:00Z"),
            mondays.GetNextOccurrence(Utc("2026-07-18T00:00:00Z")));
        Assert.Equal(
            Utc("2026-08-13T09:00:00Z"),
            thirteenths.GetNextOccurrence(Utc("2026-07-18T00:00:00Z")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("0,7")]
    public void Sunday_can_be_written_as_zero_or_seven(string sunday)
    {
        var schedule = FunctionCronSchedule.Parse($"0 12 * * {sunday}");

        Assert.Equal(
            Utc("2026-07-19T12:00:00Z"),
            schedule.GetNextOccurrence(Utc("2026-07-18T00:00:00Z")));
    }

    [Fact]
    public void GetNextOccurrence_crosses_years_and_handles_leap_day()
    {
        var schedule = FunctionCronSchedule.Parse("0 0 29 2 *");

        Assert.Equal(
            Utc("2028-02-29T00:00:00Z"),
            schedule.GetNextOccurrence(Utc("2026-07-18T00:00:00Z")));
    }

    [Fact]
    public void Evaluation_rejects_non_utc_inputs()
    {
        var schedule = FunctionCronSchedule.Parse("* * * * *");
        var localOffset = new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.FromHours(3));

        Assert.Throws<ArgumentException>(() => schedule.GetNextOccurrence(localOffset));
        Assert.Throws<ArgumentException>(() => schedule.IsMatch(localOffset));
    }

    [Fact]
    public void IsMatch_requires_an_exact_scheduled_minute()
    {
        var schedule = FunctionCronSchedule.Parse("30 10 * * *");

        Assert.True(schedule.IsMatch(Utc("2026-07-18T10:30:00Z")));
        Assert.False(schedule.IsMatch(Utc("2026-07-18T10:30:01Z")));
        Assert.False(schedule.IsMatch(Utc("2026-07-18T10:30:00Z").AddTicks(1)));
        Assert.False(schedule.IsMatch(Utc("2026-07-18T10:31:00Z")));
    }

    [Fact]
    public void Search_at_end_of_supported_calendar_fails_cleanly()
    {
        var schedule = FunctionCronSchedule.Parse("0 0 1 1 *");

        Assert.Throws<InvalidOperationException>(() =>
            schedule.GetNextOccurrence(Utc("9999-12-31T23:59:59Z")));
    }

    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value);
}
