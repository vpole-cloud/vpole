using Microsoft.Extensions.Options;

namespace Pole.Functions.Sdk;

public sealed class PoleFunctionsOptions
{
    public const string SectionName = "PoleFunctions";

    public string FunctionAppId { get; set; } = string.Empty;
    public string RevisionId { get; set; } = string.Empty;
    public int MaxConcurrencyPerInstance { get; set; } = 16;
    public int DefaultHandlerConcurrencyPerInstance { get; set; } = 16;
    public TimeSpan ReservationLease { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan AttemptRetention { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxRetainedAttempts { get; set; } = 1_000;
    public TimeSpan MaxLongPoll { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxInvocationDuration { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxRequestBodyBytes { get; set; } = 1_048_576;
    public int MaxResponseBodyBytes { get; set; } = 1_048_576;
    public int MaxProtocolRequestBytes { get; set; } = 2_097_152;
    public int MaxHeaderCount { get; set; } = 64;
    public int MaxHeaderBytes { get; set; } = 32_768;
    public int MaxPathAndQueryBytes { get; set; } = 8_192;
    public bool AllowUnauthenticatedAgentRequests { get; set; }
    public Dictionary<string, int> HandlerConcurrencyPerInstance { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class PoleFunctionsOptionsValidator : IValidateOptions<PoleFunctionsOptions>
{
    public ValidateOptionsResult Validate(string? name, PoleFunctionsOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.FunctionAppId))
            failures.Add($"{nameof(options.FunctionAppId)} is required.");
        if (string.IsNullOrWhiteSpace(options.RevisionId))
            failures.Add($"{nameof(options.RevisionId)} is required.");
        if (options.MaxConcurrencyPerInstance <= 0)
            failures.Add($"{nameof(options.MaxConcurrencyPerInstance)} must be positive.");
        if (options.DefaultHandlerConcurrencyPerInstance <= 0 ||
            options.DefaultHandlerConcurrencyPerInstance > options.MaxConcurrencyPerInstance)
        {
            failures.Add(
                $"{nameof(options.DefaultHandlerConcurrencyPerInstance)} must be between 1 and {nameof(options.MaxConcurrencyPerInstance)}.");
        }
        if (options.ReservationLease <= TimeSpan.Zero)
            failures.Add($"{nameof(options.ReservationLease)} must be positive.");
        if (options.AttemptRetention <= TimeSpan.Zero)
            failures.Add($"{nameof(options.AttemptRetention)} must be positive.");
        if (options.MaxRetainedAttempts <= 0)
            failures.Add($"{nameof(options.MaxRetainedAttempts)} must be positive.");
        if (options.MaxLongPoll <= TimeSpan.Zero)
            failures.Add($"{nameof(options.MaxLongPoll)} must be positive.");
        if (options.MaxInvocationDuration <= TimeSpan.Zero || options.MaxInvocationDuration > TimeSpan.FromDays(1))
            failures.Add($"{nameof(options.MaxInvocationDuration)} must be positive and no greater than one day.");
        if (options.MaxRequestBodyBytes <= 0)
            failures.Add($"{nameof(options.MaxRequestBodyBytes)} must be positive.");
        if (options.MaxResponseBodyBytes <= 0)
            failures.Add($"{nameof(options.MaxResponseBodyBytes)} must be positive.");
        if (options.MaxProtocolRequestBytes < ((long)options.MaxRequestBodyBytes * 4 / 3) + 65_536)
        {
            failures.Add(
                $"{nameof(options.MaxProtocolRequestBytes)} must allow for the Base64 request body and protocol envelope.");
        }
        if (options.MaxHeaderCount <= 0)
            failures.Add($"{nameof(options.MaxHeaderCount)} must be positive.");
        if (options.MaxHeaderBytes <= 0)
            failures.Add($"{nameof(options.MaxHeaderBytes)} must be positive.");
        if (options.MaxPathAndQueryBytes <= 0)
            failures.Add($"{nameof(options.MaxPathAndQueryBytes)} must be positive.");
        if (options.AttemptRetention < options.MaxLongPoll)
            failures.Add($"{nameof(options.AttemptRetention)} must be greater than or equal to {nameof(options.MaxLongPoll)}.");
        if (options.MaxRetainedAttempts < options.MaxConcurrencyPerInstance)
            failures.Add($"{nameof(options.MaxRetainedAttempts)} must be greater than or equal to {nameof(options.MaxConcurrencyPerInstance)}.");
        foreach (var (handlerName, limit) in options.HandlerConcurrencyPerInstance)
        {
            if (string.IsNullOrWhiteSpace(handlerName) || limit <= 0 || limit > options.MaxConcurrencyPerInstance)
            {
                failures.Add(
                    $"{nameof(options.HandlerConcurrencyPerInstance)} entry '{handlerName}' must be between 1 and " +
                    $"{nameof(options.MaxConcurrencyPerInstance)}.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
