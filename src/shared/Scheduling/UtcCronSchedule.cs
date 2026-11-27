// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace Pole.Scheduling;

/// <summary>
///     A five-field cron schedule evaluated in UTC. The supported fields are minute,
///     hour, day of month, month, and day of week.
/// </summary>
public sealed class UtcCronSchedule
{
    // Gregorian dates and weekdays repeat after 400 years, so one full cycle is
    // sufficient to prove that a valid five-field schedule has no future match.
    private const int MaximumSearchYears = 400;
    private readonly CronField _daysOfMonth;
    private readonly CronField _daysOfWeek;
    private readonly CronField _hours;

    private readonly CronField _minutes;
    private readonly CronField _months;

    private UtcCronSchedule(
        string expression,
        CronField minutes,
        CronField hours,
        CronField daysOfMonth,
        CronField months,
        CronField daysOfWeek)
    {
        Expression = expression;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
    }

    public string Expression { get; }

    /// <summary>
    ///     Expands fields into numeric values, using * for complete fields and 0 for Sunday.
    ///     This preserves day matching when a provider treats syntactic wildcards specially.
    /// </summary>
    public string ToCanonicalExpression() => string.Join(
        ' ',
        _minutes.ToExpression(),
        _hours.ToExpression(),
        _daysOfMonth.ToExpression(),
        _months.ToExpression(),
        _daysOfWeek.ToExpression());

    public static UtcCronSchedule Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new FormatException("A cron expression cannot be empty.");

        var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new FormatException("A cron expression must contain exactly five fields.");

        return new UtcCronSchedule(
            string.Join(' ', fields),
            CronField.Parse(fields[0], 0, 59, "minute"),
            CronField.Parse(fields[1], 0, 23, "hour"),
            CronField.Parse(fields[2], 1, 31, "day of month"),
            CronField.Parse(fields[3], 1, 12, "month"),
            CronField.Parse(fields[4], 0, 7, "day of week", normalizeSunday: true));
    }

    public static bool TryParse(string? expression, out UtcCronSchedule? schedule)
    {
        try
        {
            schedule = Parse(expression!);
            return true;
        }
        catch (FormatException)
        {
            schedule = null;
            return false;
        }
    }

    /// <summary>
    ///     Returns the first scheduled minute strictly after <paramref name="afterUtc" />.
    /// </summary>
    public DateTimeOffset GetNextOccurrence(DateTimeOffset afterUtc) => FindNextOccurrence(afterUtc) ??
        throw new InvalidOperationException("No cron occurrence exists within the supported UTC calendar range.");

    public DateTimeOffset? FindNextOccurrence(DateTimeOffset afterUtc)
    {
        EnsureUtc(afterUtc);

        var firstDate = afterUtc.UtcDateTime.Date;
        var lastYear = Math.Min(DateTime.MaxValue.Year, firstDate.Year + MaximumSearchYears);
        var lastDate = new DateTime(lastYear, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        for (var date = firstDate; date <= lastDate;)
        {
            if (_months.Contains(date.Month) && MatchesDay(date))
                foreach (var hour in _hours.Values)
                foreach (var minute in _minutes.Values)
                {
                    var candidate = new DateTimeOffset(date.Year, date.Month, date.Day, hour, minute, 0, TimeSpan.Zero);

                    if (candidate > afterUtc)
                        return candidate;
                }

            if (date == lastDate)
                break;

            date = date.AddDays(1);
        }

        return null;
    }

    public bool IsMatch(DateTimeOffset occurrenceUtc)
    {
        EnsureUtc(occurrenceUtc);

        return occurrenceUtc.Ticks % TimeSpan.TicksPerMinute == 0 && _months.Contains(occurrenceUtc.Month) &&
            _hours.Contains(occurrenceUtc.Hour) && _minutes.Contains(occurrenceUtc.Minute) &&
            MatchesDay(occurrenceUtc.UtcDateTime);
    }

    private bool MatchesDay(DateTime date)
    {
        var dayOfMonthMatches = _daysOfMonth.Contains(date.Day);
        var dayOfWeekMatches = _daysOfWeek.Contains((int)date.DayOfWeek);

        if (_daysOfMonth.IsUnrestricted)
            return _daysOfWeek.IsUnrestricted || dayOfWeekMatches;
        if (_daysOfWeek.IsUnrestricted)
            return dayOfMonthMatches;

        return dayOfMonthMatches || dayOfWeekMatches;
    }

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Cron schedules can only be evaluated with a UTC DateTimeOffset.");
    }

    private sealed class CronField
    {
        private readonly ImmutableHashSet<int> _lookup;

        private CronField(ImmutableArray<int> values, int distinctValueCount)
        {
            Values = values;
            _lookup = values.ToImmutableHashSet();
            IsUnrestricted = values.Length == distinctValueCount;
        }

        public ImmutableArray<int> Values { get; }
        public bool IsUnrestricted { get; }

        public bool Contains(int value) => _lookup.Contains(value);

        public string ToExpression() => IsUnrestricted ? "*" : string.Join(',', Values);

        public static CronField Parse(
            string expression,
            int minimum,
            int maximum,
            string fieldName,
            bool normalizeSunday = false)
        {
            if (expression.Length == 0)
                throw Invalid(fieldName);

            var values = new SortedSet<int>();
            var list = expression.Split(',');
            if (list.Any(string.IsNullOrEmpty))
                throw Invalid(fieldName);

            foreach (var item in list)
                AddItem(item, minimum, maximum, fieldName, normalizeSunday, values);

            var distinctValueCount = normalizeSunday ? maximum - minimum : maximum - minimum + 1;

            return new CronField(values.ToImmutableArray(), distinctValueCount);
        }

        private static void AddItem(
            string item,
            int minimum,
            int maximum,
            string fieldName,
            bool normalizeSunday,
            SortedSet<int> values)
        {
            var stepParts = item.Split('/');
            if (stepParts.Length is < 1 or > 2 || stepParts.Any(string.IsNullOrEmpty))
                throw Invalid(fieldName);

            var hasStep = stepParts.Length == 2;
            var step = hasStep ? ReadNumber(stepParts[1], 1, maximum - minimum + 1, fieldName) : 1;

            var rangeExpression = stepParts[0];
            int start;
            int end;

            if (rangeExpression == "*")
            {
                start = minimum;
                end = maximum;
            }
            else
            {
                var rangeParts = rangeExpression.Split('-');
                if (rangeParts.Length == 1)
                {
                    start = ReadNumber(rangeParts[0], minimum, maximum, fieldName);
                    end = hasStep ? maximum : start;
                }
                else if (rangeParts.Length == 2 && rangeParts.All(part => part.Length > 0))
                {
                    start = ReadNumber(rangeParts[0], minimum, maximum, fieldName);
                    end = ReadNumber(rangeParts[1], minimum, maximum, fieldName);
                    if (start > end)
                        throw Invalid(fieldName);
                }
                else
                {
                    throw Invalid(fieldName);
                }
            }

            for (var value = start; value <= end; value += step)
                values.Add(normalizeSunday && value == 7 ? 0 : value);
        }

        private static int ReadNumber(
            string value,
            int minimum,
            int maximum,
            string fieldName)
        {
            if (value.Length == 0 || value.Any(character => character is < '0' or > '9') ||
                !int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
                throw Invalid(fieldName);

            return parsed;
        }

        private static FormatException Invalid(string fieldName) => new($"The cron {fieldName} field is invalid.");
    }
}
