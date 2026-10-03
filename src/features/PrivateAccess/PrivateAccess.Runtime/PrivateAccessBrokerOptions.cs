using System.Net;
using Microsoft.Extensions.Options;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed class PrivateAccessBrokerOptions
{
    public const string Section = "PrivateAccess";
    public bool Enabled { get; set; }
    public int GatewayPort { get; set; } = 9444;
    public int MaximumSessions { get; set; } = 64;
    public int MaximumGatewayControls { get; set; } = 1024;
    public string GatewayImage { get; set; } = string.Empty;
    public string BrokerUri { get; set; } = string.Empty;
    public string[] BrokerAddresses { get; set; } = [];
    public int InternalPort { get; set; } = 9445;
    public string InstanceId { get; set; } = string.Empty;
    public string InternalOrigin { get; set; } = string.Empty;
    public string[] InternalPeerOrigins { get; set; } = [];
    public string InternalCertificatePath { get; set; } = string.Empty;
    public string InternalCertificatePassword { get; set; } = string.Empty;
    public string InternalCertificateSha256Pin { get; set; } = string.Empty;

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
            options.MaximumSessions is < 1 or > 1024 ||
            options.MaximumGatewayControls is < 1 or > 10000)
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
        if (options.InternalPort is < 1 or > 65535 ||
            options.InternalPort == options.GatewayPort ||
            string.IsNullOrWhiteSpace(options.InstanceId) ||
            string.IsNullOrWhiteSpace(options.InternalCertificatePath) ||
            options.InternalCertificateSha256Pin.Length != 64 ||
            !options.InternalCertificateSha256Pin.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !IsInternalOrigin(options.InternalOrigin, options.InternalPort) ||
            options.InternalPeerOrigins.Length == 0 ||
            !options.InternalPeerOrigins.Contains(options.InternalOrigin, StringComparer.Ordinal) ||
            options.InternalPeerOrigins.Distinct(StringComparer.Ordinal).Count() !=
            options.InternalPeerOrigins.Length ||
            options.InternalPeerOrigins.Any(origin => !IsInternalOrigin(origin)))
            return ValidateOptionsResult.Fail(
                "Private Access requires a distinct internal HTTPS listener, instance identity, " +
                "certificate pin and explicit peer origins.");
        return ValidateOptionsResult.Success;
    }

    private static bool IsInternalOrigin(string value, int? port = null) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && (port is null || uri.Port == port) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 && uri.AbsolutePath == "/";

    private static bool IsReachableAddress(string address) =>
        IPAddress.TryParse(address, out var parsed) &&
        !parsed.Equals(IPAddress.Any) &&
        !parsed.Equals(IPAddress.IPv6Any) &&
        !IPAddress.IsLoopback(parsed) &&
        !parsed.IsIPv6Multicast;
}
