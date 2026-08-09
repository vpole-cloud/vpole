using System.Text.RegularExpressions;

namespace Pole.KubernetesValidation;

public static partial class KubernetesValueGuard
{
    public const int MaximumLabelNameLength = 63;
    public const int MaximumDnsSubdomainLength = 253;
    public const int MaximumQualifiedNameLength =
        MaximumDnsSubdomainLength + MaximumLabelNameLength + 1;

    public static string ExtendedResourceName(
        string? value,
        string parameterName)
    {
        var normalized = Required(value, parameterName, MaximumQualifiedNameLength);
        var separator = normalized.IndexOf('/');
        if (separator <= 0 ||
            separator != normalized.LastIndexOf('/') ||
            !IsDnsSubdomain(normalized[..separator]) ||
            !IsLabelName(normalized[(separator + 1)..]) ||
            string.Equals(
                normalized[..separator],
                "kubernetes.io",
                StringComparison.Ordinal) ||
            normalized[..separator].EndsWith(
                ".kubernetes.io",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The value must be a non-Kubernetes qualified resource name.",
                parameterName);
        }

        return normalized;
    }

    public static string LabelKey(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, MaximumQualifiedNameLength);
        var separator = normalized.IndexOf('/');
        var valid = separator < 0
            ? IsLabelName(normalized)
            : separator == normalized.LastIndexOf('/') &&
              IsDnsSubdomain(normalized[..separator]) &&
              IsLabelName(normalized[(separator + 1)..]);
        return valid
            ? normalized
            : throw new ArgumentException(
                "The value must be a Kubernetes label key.",
                parameterName);
    }

    public static string LabelValue(string? value, string parameterName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= MaximumLabelNameLength &&
               (normalized.Length == 0 || IsLabelName(normalized))
            ? normalized
            : throw new ArgumentException(
                "The value must be a Kubernetes label value.",
                parameterName);
    }

    public static string? OptionalDnsSubdomain(
        string? value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return IsDnsSubdomain(normalized)
            ? normalized
            : throw new ArgumentException(
                "The value must be a Kubernetes DNS subdomain.",
                parameterName);
    }

    private static string Required(
        string? value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value is required.", parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value must be {maximumLength} characters or less.",
                parameterName);
        }

        return normalized;
    }

    private static bool IsDnsSubdomain(string value) =>
        value.Length is >= 1 and <= MaximumDnsSubdomainLength &&
        value.Split('.').All(label => DnsLabelPattern().IsMatch(label));

    private static bool IsLabelName(string value) =>
        value.Length is >= 1 and <= MaximumLabelNameLength &&
        LabelNamePattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9](?:[-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex DnsLabelPattern();

    [GeneratedRegex("^[A-Za-z0-9](?:[-_.A-Za-z0-9]*[A-Za-z0-9])?$")]
    private static partial Regex LabelNamePattern();
}
