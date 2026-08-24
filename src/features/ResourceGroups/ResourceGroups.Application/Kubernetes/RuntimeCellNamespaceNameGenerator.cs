using MongoDB.Bson;
using Microsoft.Extensions.Options;

namespace Pole.Features.ResourceGroups.Application.Kubernetes;

public delegate string GenerateRuntimeCellNamespaceName(
    string tenantId,
    string isolationScopeId);

public sealed class RuntimeCellNamespaceNameGenerator
{
    private const int MaximumPrefixLength = 24;
    private readonly string _prefix;

    public RuntimeCellNamespaceNameGenerator(KubernetesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _prefix = NormalizePrefix(options.NamespaceNamePrefix);
    }

    public string Invoke(string tenantId, string isolationScopeId)
    {
        var normalizedTenantId = NormalizeObjectId(tenantId, nameof(tenantId));
        var normalizedScopeId = NormalizeObjectId(isolationScopeId, nameof(isolationScopeId));

        return $"{_prefix}-t-{normalizedTenantId[..8]}-is-{normalizedScopeId}";
    }

    internal static string NormalizePrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The Kubernetes namespace prefix is required.", nameof(value));

        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(character =>
                (character is >= 'a' and <= 'z') ||
                char.IsAsciiDigit(character) ||
                character == '-')
            .ToArray())
            .Trim('-');

        if (normalized.Length > MaximumPrefixLength)
            normalized = normalized[..MaximumPrefixLength].TrimEnd('-');
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "The Kubernetes namespace prefix must contain at least one alphanumeric character.",
                nameof(value));
        }

        return normalized;
    }

    private static string NormalizeObjectId(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (!ObjectId.TryParse(normalized, out _))
            throw new ArgumentException("The value must be a MongoDB ObjectId.", parameterName);
        return normalized!;
    }
}

public sealed class RuntimeCellNamespaceOptionsValidator
    : IValidateOptions<KubernetesOptions>
{
    public ValidateOptionsResult Validate(string? name, KubernetesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            RuntimeCellNamespaceNameGenerator.NormalizePrefix(options.NamespaceNamePrefix);
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
