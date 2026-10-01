using System.Net;
using Microsoft.Extensions.Options;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed class PrivateAccessBrokerOptions
{
    public const string Section = "PrivateAccess";
    public bool Enabled { get; set; }
    public int ConnectorPort { get; set; } = 9444;
    public int MaximumSessions { get; set; } = 64;
    public string WorkerNamespace { get; set; } = "pole-private-access";
    public string WorkerImage { get; set; } = string.Empty;
    public string BrokerUri { get; set; } = string.Empty;
    public string[] BrokerAddresses { get; set; } = [];

    public PrivateAccessWorkerSettings WorkerSettings() => new(
        WorkerNamespace,
        WorkerImage,
        new Uri(BrokerUri),
        BrokerAddresses.Select(IPAddress.Parse).ToArray());
}

public sealed class PrivateAccessBrokerOptionsValidator : IValidateOptions<PrivateAccessBrokerOptions>
{
    public ValidateOptionsResult Validate(string? name, PrivateAccessBrokerOptions options)
    {
        if (!options.Enabled)
            return ValidateOptionsResult.Success;
        if (options.ConnectorPort is < 1 or > 65535 || options.MaximumSessions is < 1 or > 1024)
            return ValidateOptionsResult.Fail("Private Access listener or session limits are invalid.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(options.WorkerNamespace,
                "^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$"))
            return ValidateOptionsResult.Fail("Private Access worker namespace must be a DNS label.");
        if (string.IsNullOrWhiteSpace(options.WorkerImage) || options.WorkerImage.EndsWith(":latest", StringComparison.Ordinal))
            return ValidateOptionsResult.Fail("Private Access requires an explicit, versioned worker image.");
        if (!Uri.TryCreate(options.BrokerUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            return ValidateOptionsResult.Fail("Private Access broker URI must be an HTTPS origin.");
        if (options.BrokerAddresses.Length == 0 || options.BrokerAddresses.Any(address =>
                !IPAddress.TryParse(address, out var parsed) ||
                parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any) ||
                IPAddress.IsLoopback(parsed) || parsed.IsIPv6Multicast))
            return ValidateOptionsResult.Fail("Private Access requires exact reachable broker IP addresses for worker egress.");
        return ValidateOptionsResult.Success;
    }
}
