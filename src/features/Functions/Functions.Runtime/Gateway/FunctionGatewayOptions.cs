using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Pole.Features.Functions.Runtime.Gateway;

public sealed class FunctionGatewayOptions
{
    public const string SectionName = "Functions:Gateway";

    [Range(1, 65_535)]
    public int ControlPort { get; set; } = 8080;

    [Range(1, 65_535)]
    public int PublicPort { get; set; } = 8081;

    [Range(1, 65_535)]
    public int InternalPort { get; set; } = 8082;

    [Range(1, int.MaxValue)]
    public int MaxRequestBodyBytes { get; set; } = 1_048_576;

    [Range(1, int.MaxValue)]
    public int MaxResponseBodyBytes { get; set; } = 1_048_576;

    [Range(1, int.MaxValue)]
    public int MaxHeaderCount { get; set; } = 64;

    [Range(1, int.MaxValue)]
    public int MaxHeaderBytes { get; set; } = 32_768;

    [Range(1, int.MaxValue)]
    public int MaxPathAndQueryBytes { get; set; } = 8_192;

    [Range(1, int.MaxValue)]
    public int MaxPendingRequests { get; set; } = 1_024;

    [Range(1, int.MaxValue)]
    public int MaxPendingRequestsPerRevision { get; set; } = 100;

    [Range(typeof(long), "1", "9223372036854775807")]
    public long MaxPendingBytes { get; set; } = 64L * 1024 * 1024;

    [Range(typeof(long), "1", "9223372036854775807")]
    public long MaxPendingBytesPerRevision { get; set; } = 16L * 1024 * 1024;

    [Range(1, int.MaxValue)]
    public int PublicRequestsPerSecond { get; set; } = 100;

    [Range(1, int.MaxValue)]
    public int PublicBurst { get; set; } = 200;

    [Range(1, 900)]
    public int ExecutionTimeoutSeconds { get; set; } = 120;

    [Range(1, 300)]
    public int RequestBodyTimeoutSeconds { get; set; } = 30;

    [Range(1, 30_000)]
    public int AgentLongPollMilliseconds { get; set; } = 25_000;

    [Range(5, 1_000)]
    public int PostStartMeterWriteTimeoutMilliseconds { get; set; } = 100;
    public HashSet<string> ForwardedRequestHeaders { get; set; } = new(
        ["Accept", "Content-Type", "User-Agent", "X-Request-ID"],
        StringComparer.OrdinalIgnoreCase);

}

public sealed class FunctionGatewayOptionsValidator : IValidateOptions<FunctionGatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, FunctionGatewayOptions options)
    {
        var failures = FunctionOptionsValidation.GetDataAnnotationFailures(options);
        if (options.ControlPort == options.PublicPort ||
            options.ControlPort == options.InternalPort ||
            options.PublicPort == options.InternalPort)
        {
            failures.Add("Control and Function Gateway ports must be distinct.");
        }
        if (options.MaxPendingRequestsPerRevision > options.MaxPendingRequests)
        {
            failures.Add(
                $"{nameof(options.MaxPendingRequestsPerRevision)} cannot exceed {nameof(options.MaxPendingRequests)}.");
        }
        if (options.MaxPendingBytesPerRevision > options.MaxPendingBytes)
        {
            failures.Add(
                $"{nameof(options.MaxPendingBytesPerRevision)} cannot exceed {nameof(options.MaxPendingBytes)}.");
        }
        if (options.PublicBurst < options.PublicRequestsPerSecond)
        {
            failures.Add(
                $"{nameof(options.PublicBurst)} cannot be lower than {nameof(options.PublicRequestsPerSecond)}.");
        }

        return FunctionOptionsValidation.ToValidateOptionsResult(failures);
    }
}
