using Pole.Workloads;

namespace Pole.Features.Functions.Domain;

public sealed class FunctionCronSchedule
{
    private readonly UtcCronSchedule _schedule;
    private FunctionCronSchedule(UtcCronSchedule schedule) => _schedule = schedule;
    public string Expression => _schedule.Expression;
    public static FunctionCronSchedule Parse(string expression) => new(UtcCronSchedule.Parse(expression));

    public static bool TryParse(string? expression, out FunctionCronSchedule? schedule)
    {
        schedule = UtcCronSchedule.TryParse(expression, out var parsed) ? new(parsed!) : null;
        return schedule is not null;
    }

    public DateTimeOffset GetNextOccurrence(DateTimeOffset afterUtc) => _schedule.GetNextOccurrence(afterUtc);
    public bool IsMatch(DateTimeOffset occurrenceUtc) => _schedule.IsMatch(occurrenceUtc);
}
