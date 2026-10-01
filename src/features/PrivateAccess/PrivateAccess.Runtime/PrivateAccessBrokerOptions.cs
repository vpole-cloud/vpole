using System.Net;
using Microsoft.Extensions.Options;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed class PrivateAccessBrokerOptions
{
    public const string Section = "PrivateAccess";
    public bool Enabled { get; set; }
    public int GatewayPort { get; set; } = 9444;
    public int MaximumSessions { get; set; } = 64;
    public string GatewayImage { get; set; } = string.Empty;
    public string BrokerUri { get; set; } = string.Empty;
    public string[] BrokerAddresses { get; set; } = [];

    public PrivateAccessGatewaySettings GatewaySettings() => new(
        GatewayImage,
        new UriBuilder(BrokerUri) { Port = GatewayPort }.Uri,
        BrokerAddresses.Select(IPAddress.Parse).ToArray());
}

public sealed class PrivateAccessBrokerOptionsValidator : IValidateOptions<PrivateAccessBrokerOptions>
{
    public ValidateOptionsResult Validate(string? name, PrivateAccessBrokerOptions options)
    {
        if (!options.Enabled)
            return ValidateOptionsResult.Success;
        if (options.GatewayPort is < 1 or > 65535 ||
            options.MaximumSessions is < 1 or > 1024)
            return ValidateOptionsResult.Fail("Private Access listener or session limits are invalid.");
        if (string.IsNullOrWhiteSpace(options.GatewayImage) ||
            options.GatewayImage.EndsWith(":latest", StringComparison.Ordinal))
            return ValidateOptionsResult.Fail("Private Access requires an explicit, versioned gateway image.");
        if (!Uri.TryCreate(options.BrokerUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            return ValidateOptionsResult.Fail("Private Access broker URI must be an HTTPS origin.");
        if (options.BrokerAddresses.Length == 0 ||
            options.BrokerAddresses.Any(address => !IsReachableAddress(address)))
            return ValidateOptionsResult.Fail(
                "Private Access requires exact reachable broker IP addresses for gateway egress.");
        return ValidateOptionsResult.Success;
    }

    private static bool IsReachableAddress(string address) =>
        IPAddress.TryParse(address, out var parsed) &&
        !parsed.Equals(IPAddress.Any) &&
        !parsed.Equals(IPAddress.IPv6Any) &&
        !IPAddress.IsLoopback(parsed) &&
        !parsed.IsIPv6Multicast;
}
