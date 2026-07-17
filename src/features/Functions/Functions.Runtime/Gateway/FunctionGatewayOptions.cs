namespace Pole.Features.Functions.Runtime.Gateway;

public sealed class FunctionGatewayOptions
{
    public const string SectionName = "Functions:Gateway";

    public int ControlPort { get; set; } = 8080;
    public int PublicPort { get; set; } = 8081;
    public int InternalPort { get; set; } = 8082;
    public int MaxRequestBodyBytes { get; set; } = 1_048_576;
    public int MaxResponseBodyBytes { get; set; } = 1_048_576;
    public int MaxHeaderCount { get; set; } = 64;
    public int MaxHeaderBytes { get; set; } = 32_768;
    public int MaxPathAndQueryBytes { get; set; } = 8_192;
    public int MaxPendingRequests { get; set; } = 1_024;
    public int MaxPendingRequestsPerRevision { get; set; } = 100;
    public long MaxPendingBytes { get; set; } = 64L * 1024 * 1024;
    public long MaxPendingBytesPerRevision { get; set; } = 16L * 1024 * 1024;
    public int PublicRequestsPerSecond { get; set; } = 100;
    public int PublicBurst { get; set; } = 200;
    public int ExecutionTimeoutSeconds { get; set; } = 120;
    public int AgentLongPollMilliseconds { get; set; } = 25_000;
    public HashSet<string> ForwardedRequestHeaders { get; set; } = new(
        ["Accept", "Content-Type", "User-Agent", "X-Request-ID"],
        StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (ControlPort is <= 0 or > 65535 || PublicPort is <= 0 or > 65535 ||
            InternalPort is <= 0 or > 65535 ||
            ControlPort == PublicPort || ControlPort == InternalPort || PublicPort == InternalPort)
            throw new InvalidOperationException("Control and Function Gateway ports must be valid and distinct.");
        if (MaxRequestBodyBytes <= 0 || MaxResponseBodyBytes <= 0 ||
            MaxHeaderCount <= 0 || MaxHeaderBytes <= 0 ||
            MaxPathAndQueryBytes <= 0)
            throw new InvalidOperationException("Function Gateway request limits must be positive.");
        if (MaxPendingRequests <= 0 || MaxPendingRequestsPerRevision <= 0 ||
            MaxPendingRequestsPerRevision > MaxPendingRequests ||
            MaxPendingBytes <= 0 || MaxPendingBytesPerRevision <= 0 ||
            MaxPendingBytesPerRevision > MaxPendingBytes)
            throw new InvalidOperationException("Function Gateway pending limits are invalid.");
        if (PublicRequestsPerSecond <= 0 || PublicBurst < PublicRequestsPerSecond)
            throw new InvalidOperationException("Function Gateway public rate and burst limits are invalid.");
        if (ExecutionTimeoutSeconds is <= 0 or > 900 || AgentLongPollMilliseconds is <= 0 or > 30_000)
            throw new InvalidOperationException("Function Gateway execution deadlines are invalid.");
    }
}
