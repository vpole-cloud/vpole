using System.Globalization;
using Microsoft.Extensions.Options;

namespace Pole.Features.Outbox.Application;

public sealed class OutboxSettings
{
    public const string SectionName = "Outbox";

    public int BatchSize { get; set; } = 100;
    public string ProcessingCronExpression { get; set; } = "* * * * *";
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
    public int PublishedRetentionDays { get; set; } = 7;
    public int AbandonedRetentionDays { get; set; } = 30;
    public int CleanupBatchSize { get; set; } = 500;
}

public sealed class OutboxSettingsValidator : IValidateOptions<OutboxSettings>
{
    public ValidateOptionsResult Validate(string? name, OutboxSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        RequirePositive(options.BatchSize, nameof(options.BatchSize), failures);
        if (!OutboxCronExpression.IsValid(options.ProcessingCronExpression))
        {
            failures.Add(
                $"{OutboxSettings.SectionName}:{nameof(options.ProcessingCronExpression)} " +
                "must be a valid five-field cron expression.");
        }

        RequirePositive(
            options.InitialRetryDelay,
            nameof(options.InitialRetryDelay),
            failures);
        RequirePositive(
            options.MaximumRetryDelay,
            nameof(options.MaximumRetryDelay),
            failures);
        if (options.MaximumRetryDelay < options.InitialRetryDelay)
        {
            failures.Add(
                $"{OutboxSettings.SectionName}:{nameof(options.MaximumRetryDelay)} must be " +
                $"greater than or equal to {nameof(options.InitialRetryDelay)}.");
        }

        RequirePositive(
            options.PublishedRetentionDays,
            nameof(options.PublishedRetentionDays),
            failures);
        RequirePositive(
            options.AbandonedRetentionDays,
            nameof(options.AbandonedRetentionDays),
            failures);
        if (options.AbandonedRetentionDays < options.PublishedRetentionDays)
        {
            failures.Add(
                $"{OutboxSettings.SectionName}:{nameof(options.AbandonedRetentionDays)} must be " +
                $"greater than or equal to {nameof(options.PublishedRetentionDays)}.");
        }

        RequirePositive(options.CleanupBatchSize, nameof(options.CleanupBatchSize), failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePositive(
        int value,
        string propertyName,
        ICollection<string> failures)
    {
        if (value <= 0)
        {
            failures.Add(
                $"{OutboxSettings.SectionName}:{propertyName} must be greater than zero.");
        }
    }

    private static void RequirePositive(
        TimeSpan value,
        string propertyName,
        ICollection<string> failures)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add(
                $"{OutboxSettings.SectionName}:{propertyName} must be greater than zero.");
        }
    }
}

internal static class OutboxCronExpression
{
    private static readonly (int Minimum, int Maximum)[] FiveFieldRanges =
    [
        (0, 59),
        (0, 23),
        (1, 31),
        (1, 12),
        (0, 7)
    ];

    public static bool IsValid(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var fields = expression.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return fields.Length == FiveFieldRanges.Length &&
               fields.Select((field, index) =>
                       IsValidField(field, FiveFieldRanges[index]))
                   .All(valid => valid);
    }

    private static bool IsValidField(
        string field,
        (int Minimum, int Maximum) range) =>
        field.Length > 0 &&
        field.Split(',', StringSplitOptions.TrimEntries)
            .All(part => IsValidPart(part, range));

    private static bool IsValidPart(
        string part,
        (int Minimum, int Maximum) range)
    {
        var stepParts = part.Split('/');
        if (stepParts.Length > 2 ||
            stepParts.Length == 2 &&
            !TryReadNumber(
                stepParts[1],
                minimum: 1,
                maximum: range.Maximum - range.Minimum + 1,
                out _))
        {
            return false;
        }

        var value = stepParts[0];
        if (value == "*")
            return true;

        var rangeParts = value.Split('-');
        if (rangeParts.Length == 1)
        {
            return TryReadNumber(
                rangeParts[0],
                range.Minimum,
                range.Maximum,
                out _);
        }

        return rangeParts.Length == 2 &&
               TryReadNumber(
                   rangeParts[0],
                   range.Minimum,
                   range.Maximum,
                   out var start) &&
               TryReadNumber(
                   rangeParts[1],
                   range.Minimum,
                   range.Maximum,
                   out var end) &&
               start <= end;
    }

    private static bool TryReadNumber(
        string value,
        int minimum,
        int maximum,
        out int parsed) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out parsed) &&
        parsed >= minimum &&
        parsed <= maximum;
}
