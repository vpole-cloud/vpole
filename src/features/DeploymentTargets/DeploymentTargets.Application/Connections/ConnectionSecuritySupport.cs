using System.Security.Cryptography;
using System.Text;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Connections;

internal static class ConnectionPayloadLimits
{
    public const int MaximumKubeconfigBytes = 1024 * 1024;
    public const int MaximumTokenBytes = 64 * 1024;
    public const int MaximumCertificateDataCharacters = 1024 * 1024;
    public const int MaximumServiceAccountCredentialBytes =
        MaximumCertificateDataCharacters + MaximumTokenBytes + (4 * 1024);
    public const int MaximumContextNameCharacters = 253;
    public const int MaximumServerUrlCharacters = 2048;
    public const int MaximumTlsServerNameCharacters = 253;
    public const int MaximumDeploymentTargetIdCharacters = 512;
}

internal static class ConnectionFailures
{
    public static ValidationFailure Validation(string field, string message) =>
        Failures.ValidationFailure(new Dictionary<string, string[]>
        {
            [field] = [message]
        });

    public static InternalFailure StoredConnectionUnavailable() =>
        Failures.Internal(
            "The deployment-target connection could not be loaded.",
            "Stored connection material is invalid or unavailable.");

    public static InternalFailure ConnectionProtectionUnavailable() =>
        Failures.Internal(
            "The deployment-target connection could not be protected.",
            "The configured connection-protection service is unavailable.");

    public static Failure ClientConfigurationFailure(bool storedConnection) =>
        storedConnection
            ? StoredConnectionUnavailable()
            : Validation("connection", "The Kubernetes connection configuration is invalid.");
}

internal static class ConnectionValueValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryEncodeBounded(
        string? value,
        int maximumBytes,
        out byte[] encoded)
    {
        encoded = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
                return false;
            encoded = StrictUtf8.GetBytes(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    public static bool HasValidTokenShape(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > ConnectionPayloadLimits.MaximumTokenBytes)
            return false;

        if (!TryEncodeBounded(token, ConnectionPayloadLimits.MaximumTokenBytes, out var tokenBytes))
            return false;

        CryptographicOperations.ZeroMemory(tokenBytes);

        var paddingStarted = false;
        foreach (var character in token)
        {
            if (character == '=')
            {
                paddingStarted = true;
                continue;
            }

            if (paddingStarted ||
                !(character is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or
                    '-' or '.' or '_' or '~' or '+' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryNormalizeBase64(
        string? value,
        int maximumCharacters,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters)
            return false;

        byte[]? decoded = null;
        try
        {
            decoded = Convert.FromBase64String(value);
            if (decoded.Length == 0)
                return false;
            normalized = Convert.ToBase64String(decoded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (decoded is not null)
                CryptographicOperations.ZeroMemory(decoded);
        }
    }

    public static bool TryNormalizeServerUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > ConnectionPayloadLimits.MaximumServerUrlCharacters)
            return false;

        var candidate = value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = candidate.TrimEnd('/');
        return normalized.Length > 0;
    }

    public static bool TryNormalizeTlsServerName(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var candidate = value.Trim();
        if (candidate.Length > ConnectionPayloadLimits.MaximumTlsServerNameCharacters ||
            candidate.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return false;
        }

        var hostKind = Uri.CheckHostName(candidate);
        if (hostKind is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            return false;

        normalized = candidate;
        return true;
    }
}
