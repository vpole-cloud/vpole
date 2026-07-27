using MongoDB.Bson;

namespace Pole.Features.ResourceGroups.Domain;

internal static class ResourceGroupValueGuard
{
    public const int MaximumResourceGroupNameLength = 20;
    public const int MaximumResourceGroupDescriptionLength = 200;
    public const int MaximumResourceGroupTagLength = 30;
    public const int MaximumIsolationScopeNameLength = 80;
    public const int MaximumKubernetesNamespaceLength = 63;
    public const int MaximumRuntimeCellFailureCodeLength = 128;
    public const int MaximumRuntimeCellFailureMessageLength = 512;

    public static string Required(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value is required.", parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
            throw new ArgumentException($"The value must be {maximumLength} characters or less.", parameterName);
        return normalized;
    }

    public static string Optional(string? value, string parameterName, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximumLength)
            throw new ArgumentException($"The value must be {maximumLength} characters or less.", parameterName);
        return normalized;
    }

    public static string ObjectId(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, 24);
        if (!MongoDB.Bson.ObjectId.TryParse(normalized, out _))
            throw new ArgumentException("The value must be a MongoDB ObjectId.", parameterName);
        return normalized;
    }

    public static DateTime Utc(DateTime value, string parameterName)
    {
        if (value == default)
            throw new ArgumentException("The timestamp is required.", parameterName);

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    public static DateTime UpdatedUtc(DateTime value, DateTime currentUtc, string parameterName)
    {
        var normalized = Utc(value, parameterName);
        if (normalized < currentUtc)
            throw new ArgumentException("The update time cannot move backwards.", parameterName);
        return normalized;
    }

    public static string KubernetesNamespace(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, MaximumKubernetesNamespaceLength);
        if (!IsDnsLabel(normalized))
        {
            throw new ArgumentException(
                "The Kubernetes namespace must be a lowercase DNS label.",
                parameterName);
        }

        return normalized;
    }

    private static bool IsDnsLabel(string value)
    {
        if (value.Length is < 1 or > MaximumKubernetesNamespaceLength ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            !char.IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }

        return value.All(character =>
            (character is >= 'a' and <= 'z') ||
            char.IsAsciiDigit(character) ||
            character == '-');
    }
}
