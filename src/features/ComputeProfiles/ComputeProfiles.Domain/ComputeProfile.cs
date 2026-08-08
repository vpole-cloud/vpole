using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ComputeProfiles.Domain;

public sealed record ComputeProfile(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string DisplayName,
    string Description,
    ComputeProfileResourceRequirements Resources,
    ComputeArchitecture Architecture,
    ImmutableList<ComputeProfileExtendedResource> ExtendedResources,
    ImmutableHashSet<string> AllowedResourceTypes,
    ComputeProfileTenantAccess TenantAccess,
    ImmutableList<ComputeProfileTargetMapping> TargetMappings,
    bool IsEnabled,
    bool IsDefault,
    long Revision,
    long ResolutionFence,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public ComputeProfile Reconfigure(
        string displayName,
        string description,
        ComputeProfileResourceRequirements resources,
        ComputeArchitecture architecture,
        IEnumerable<ComputeProfileExtendedResource> extendedResources,
        IEnumerable<ResourceTypeId> allowedResourceTypes,
        ComputeProfileTenantAccess tenantAccess,
        IEnumerable<ComputeProfileTargetMapping> targetMappings,
        bool isEnabled,
        bool isDefault,
        DateTime updatedAtUtc)
    {
        var values = ComputeProfileFactory.Normalize(
            displayName,
            description,
            resources,
            architecture,
            extendedResources,
            allowedResourceTypes,
            tenantAccess,
            targetMappings,
            isEnabled,
            isDefault);
        if (HasSameConfiguration(values))
            return this;

        var normalizedUpdatedAtUtc = ComputeProfileValueGuard.Utc(
            updatedAtUtc,
            nameof(updatedAtUtc));
        if (normalizedUpdatedAtUtc < UpdatedAtUtc)
        {
            throw new ArgumentException(
                "The update time cannot move backwards.",
                nameof(updatedAtUtc));
        }

        return new ComputeProfile(
            Id,
            values.DisplayName,
            values.Description,
            values.Resources,
            values.Architecture,
            values.ExtendedResources,
            values.AllowedResourceTypes,
            values.TenantAccess,
            values.TargetMappings,
            values.IsEnabled,
            values.IsDefault,
            checked(Revision + 1),
            ResolutionFence,
            CreatedAtUtc,
            normalizedUpdatedAtUtc);
    }

    private bool HasSameConfiguration(ComputeProfileValues values) =>
        string.Equals(DisplayName, values.DisplayName, StringComparison.Ordinal) &&
        string.Equals(Description, values.Description, StringComparison.Ordinal) &&
        Equals(Resources, values.Resources) &&
        Architecture == values.Architecture &&
        ExtendedResources.SequenceEqual(values.ExtendedResources) &&
        AllowedResourceTypes.SetEquals(values.AllowedResourceTypes) &&
        HasSameTenantAccess(TenantAccess, values.TenantAccess) &&
        TargetMappings.Select(mapping => mapping.ConfigurationKey).SequenceEqual(
            values.TargetMappings.Select(mapping => mapping.ConfigurationKey),
            StringComparer.Ordinal) &&
        IsEnabled == values.IsEnabled &&
        IsDefault == values.IsDefault;

    private static bool HasSameTenantAccess(
        ComputeProfileTenantAccess current,
        ComputeProfileTenantAccess replacement) =>
        (current, replacement) switch
        {
            (AllTenantsComputeProfileAccess, AllTenantsComputeProfileAccess) => true,
            (SelectedTenantsComputeProfileAccess first,
                SelectedTenantsComputeProfileAccess second) =>
                first.TenantIds.SetEquals(second.TenantIds),
            _ => false
        };
}

public sealed record ComputeProfileResourceRequirements(
    ComputeProfileResourceQuantity Requests,
    ComputeProfileResourceQuantity Limits)
{
    public static ComputeProfileResourceRequirements Create(
        ComputeProfileResourceQuantity requests,
        ComputeProfileResourceQuantity limits)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(limits);
        var normalizedRequests = ComputeProfileResourceQuantity.Create(
            requests.CpuMillis,
            requests.MemoryMi);
        var normalizedLimits = ComputeProfileResourceQuantity.Create(
            limits.CpuMillis,
            limits.MemoryMi);
        if (normalizedLimits.CpuMillis < normalizedRequests.CpuMillis ||
            normalizedLimits.MemoryMi < normalizedRequests.MemoryMi)
        {
            throw new ArgumentException(
                "Compute limits must be greater than or equal to requests.",
                nameof(limits));
        }

        return new ComputeProfileResourceRequirements(
            normalizedRequests,
            normalizedLimits);
    }
}

public sealed record ComputeProfileResourceQuantity(
    long CpuMillis,
    long MemoryMi)
{
    public static ComputeProfileResourceQuantity Create(
        long cpuMillis,
        long memoryMi) =>
        new(
            ComputeProfileValueGuard.Positive(cpuMillis, nameof(cpuMillis)),
            ComputeProfileValueGuard.Positive(memoryMi, nameof(memoryMi)));
}

public sealed record ComputeProfileExtendedResource(
    string Name,
    long Quantity)
{
    public static ComputeProfileExtendedResource Create(
        string name,
        long quantity) =>
        new(
            ComputeProfileValueGuard.ExtendedResourceName(name, nameof(name)),
            ComputeProfileValueGuard.Positive(quantity, nameof(quantity)));
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(AllTenantsComputeProfileAccess),
    typeof(SelectedTenantsComputeProfileAccess))]
public abstract record ComputeProfileTenantAccess;

[BsonDiscriminator(ComputeProfileTenantAccessKinds.AllTenants)]
public sealed record AllTenantsComputeProfileAccess : ComputeProfileTenantAccess
{
    public static AllTenantsComputeProfileAccess Create() => new();
}

[BsonDiscriminator(ComputeProfileTenantAccessKinds.SelectedTenants)]
public sealed record SelectedTenantsComputeProfileAccess(
    ImmutableHashSet<string> TenantIds) : ComputeProfileTenantAccess
{
    public static SelectedTenantsComputeProfileAccess Create(
        IEnumerable<string> tenantIds)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        var normalized = tenantIds
            .Select(tenantId => ComputeProfileValueGuard.ObjectId(
                tenantId,
                nameof(tenantIds)))
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (normalized.Count == 0)
        {
            throw new ArgumentException(
                "At least one tenant ID is required.",
                nameof(tenantIds));
        }

        return new SelectedTenantsComputeProfileAccess(normalized);
    }
}

public static class ComputeProfileTenantAccessKinds
{
    public const string AllTenants = "all-tenants";
    public const string SelectedTenants = "selected-tenants";
}

public sealed record ComputeProfileTargetMapping(
    [property: BsonRepresentation(BsonType.ObjectId)]
    string DeploymentTargetId,
    ImmutableList<ComputeProfileNodeSelector> NodeSelector,
    ImmutableList<ComputeProfileNodeAffinityTerm> RequiredNodeAffinity,
    ImmutableList<ComputeProfilePreferredNodeAffinityTerm> PreferredNodeAffinity,
    ImmutableList<ComputeProfileToleration> Tolerations,
    string? RuntimeClassName)
{
    [BsonIgnore]
    internal string ConfigurationKey => string.Join(
        "||",
        DeploymentTargetId,
        string.Join('|', NodeSelector.Select(item => $"{item.Key}={item.Value}")),
        string.Join('|', RequiredNodeAffinity.Select(term => term.SortKey)),
        string.Join('|', PreferredNodeAffinity.Select(term => $"{term.Weight}:{term.SortKey}")),
        string.Join('|', Tolerations.Select(toleration => toleration.SortKey)),
        RuntimeClassName ?? string.Empty);

    public static ComputeProfileTargetMapping Create(
        string deploymentTargetId,
        IEnumerable<ComputeProfileNodeSelector>? nodeSelector = null,
        IEnumerable<ComputeProfileNodeAffinityTerm>? requiredNodeAffinity = null,
        IEnumerable<ComputeProfilePreferredNodeAffinityTerm>? preferredNodeAffinity = null,
        IEnumerable<ComputeProfileToleration>? tolerations = null,
        string? runtimeClassName = null)
    {
        var normalizedSelector = (nodeSelector ?? [])
            .Select(item => ComputeProfileNodeSelector.Create(item.Key, item.Value))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .ToImmutableList();
        if (normalizedSelector.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() !=
            normalizedSelector.Count)
        {
            throw new ArgumentException(
                "Node selector keys must be unique.",
                nameof(nodeSelector));
        }

        var normalizedRequired = (requiredNodeAffinity ?? [])
            .Select(term => ComputeProfileNodeAffinityTerm.Create(term.Expressions))
            .OrderBy(term => term.SortKey, StringComparer.Ordinal)
            .ToImmutableList();
        var normalizedPreferred = (preferredNodeAffinity ?? [])
            .Select(term => ComputeProfilePreferredNodeAffinityTerm.Create(
                term.Weight,
                term.Term))
            .OrderByDescending(term => term.Weight)
            .ThenBy(term => term.SortKey, StringComparer.Ordinal)
            .ToImmutableList();
        var normalizedTolerations = (tolerations ?? [])
            .Select(toleration => ComputeProfileToleration.Create(
                toleration.Key,
                toleration.Operator,
                toleration.Value,
                toleration.Effect,
                toleration.TolerationSeconds))
            .OrderBy(toleration => toleration.SortKey, StringComparer.Ordinal)
            .ToImmutableList();

        return new ComputeProfileTargetMapping(
            ComputeProfileValueGuard.ObjectId(
                deploymentTargetId,
                nameof(deploymentTargetId)),
            normalizedSelector,
            normalizedRequired,
            normalizedPreferred,
            normalizedTolerations,
            ComputeProfileValueGuard.OptionalDnsSubdomain(
                runtimeClassName,
                nameof(runtimeClassName)));
    }
}

public sealed record ComputeProfileNodeSelector(
    string Key,
    string Value)
{
    public static ComputeProfileNodeSelector Create(
        string key,
        string value) =>
        new(
            ComputeProfileValueGuard.LabelKey(key, nameof(key)),
            ComputeProfileValueGuard.LabelValue(value, nameof(value)));
}

public sealed record ComputeProfileNodeAffinityExpression(
    string Key,
    ComputeNodeAffinityOperator Operator,
    ImmutableList<string> Values)
{
    public static ComputeProfileNodeAffinityExpression Create(
        string key,
        ComputeNodeAffinityOperator @operator,
        IEnumerable<string>? values)
    {
        if (!Enum.IsDefined(@operator))
            throw new ArgumentOutOfRangeException(nameof(@operator));

        var normalizedValues = (values ?? [])
            .Select(value => ComputeProfileValueGuard.LabelValue(
                value,
                nameof(values)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableList();
        switch (@operator)
        {
            case ComputeNodeAffinityOperator.In:
            case ComputeNodeAffinityOperator.NotIn:
                if (normalizedValues.Count == 0)
                {
                    throw new ArgumentException(
                        "In and NotIn affinity expressions require at least one value.",
                        nameof(values));
                }
                break;
            case ComputeNodeAffinityOperator.Exists:
            case ComputeNodeAffinityOperator.DoesNotExist:
                if (normalizedValues.Count != 0)
                {
                    throw new ArgumentException(
                        "Exists and DoesNotExist affinity expressions cannot contain values.",
                        nameof(values));
                }
                break;
            case ComputeNodeAffinityOperator.GreaterThan:
            case ComputeNodeAffinityOperator.LessThan:
                if (normalizedValues.Count != 1 ||
                    !long.TryParse(normalizedValues[0], out _))
                {
                    throw new ArgumentException(
                        "GreaterThan and LessThan affinity expressions require one integer value.",
                        nameof(values));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(@operator));
        }

        return new ComputeProfileNodeAffinityExpression(
            ComputeProfileValueGuard.LabelKey(key, nameof(key)),
            @operator,
            normalizedValues);
    }
}

public sealed record ComputeProfileNodeAffinityTerm(
    ImmutableList<ComputeProfileNodeAffinityExpression> Expressions)
{
    [BsonIgnore]
    internal string SortKey => string.Join(
        '|',
        Expressions.Select(expression =>
            $"{expression.Key}:{expression.Operator}:{string.Join(',', expression.Values)}"));

    public static ComputeProfileNodeAffinityTerm Create(
        IEnumerable<ComputeProfileNodeAffinityExpression> expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        var normalized = expressions
            .Select(expression => ComputeProfileNodeAffinityExpression.Create(
                expression.Key,
                expression.Operator,
                expression.Values))
            .OrderBy(expression => expression.Key, StringComparer.Ordinal)
            .ThenBy(expression => expression.Operator)
            .ThenBy(expression => string.Join(',', expression.Values), StringComparer.Ordinal)
            .ToImmutableList();
        if (normalized.Count == 0)
            throw new ArgumentException("An affinity term requires an expression.", nameof(expressions));

        return new ComputeProfileNodeAffinityTerm(normalized);
    }
}

public sealed record ComputeProfilePreferredNodeAffinityTerm(
    int Weight,
    ComputeProfileNodeAffinityTerm Term)
{
    [BsonIgnore]
    internal string SortKey => Term.SortKey;

    public static ComputeProfilePreferredNodeAffinityTerm Create(
        int weight,
        ComputeProfileNodeAffinityTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (weight is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(weight), "Affinity weight must be between 1 and 100.");

        return new ComputeProfilePreferredNodeAffinityTerm(
            weight,
            ComputeProfileNodeAffinityTerm.Create(term.Expressions));
    }
}

public sealed record ComputeProfileToleration(
    string? Key,
    ComputeTolerationOperator Operator,
    string? Value,
    ComputeTaintEffect Effect,
    long? TolerationSeconds)
{
    [BsonIgnore]
    internal string SortKey =>
        $"{Key}:{Operator}:{Value}:{Effect}:{TolerationSeconds}";

    public static ComputeProfileToleration Create(
        string? key,
        ComputeTolerationOperator @operator,
        string? value,
        ComputeTaintEffect effect,
        long? tolerationSeconds)
    {
        if (!Enum.IsDefined(@operator))
            throw new ArgumentOutOfRangeException(nameof(@operator));
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect));

        var normalizedKey = string.IsNullOrWhiteSpace(key)
            ? null
            : ComputeProfileValueGuard.LabelKey(key, nameof(key));
        var normalizedValue = value is null
            ? null
            : ComputeProfileValueGuard.LabelValue(value, nameof(value));
        if (@operator == ComputeTolerationOperator.Equal &&
            (normalizedKey is null || normalizedValue is null))
        {
            throw new ArgumentException(
                "Equal tolerations require both key and value.",
                nameof(value));
        }
        if (@operator == ComputeTolerationOperator.Exists && normalizedValue is not null)
        {
            throw new ArgumentException(
                "Exists tolerations cannot contain a value.",
                nameof(value));
        }
        if (normalizedKey is null && @operator != ComputeTolerationOperator.Exists)
            throw new ArgumentException("An empty toleration key requires the Exists operator.", nameof(key));
        if (tolerationSeconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerationSeconds));
        if (tolerationSeconds is not null && effect != ComputeTaintEffect.NoExecute)
        {
            throw new ArgumentException(
                "TolerationSeconds is supported only for NoExecute tolerations.",
                nameof(tolerationSeconds));
        }

        return new ComputeProfileToleration(
            normalizedKey,
            @operator,
            normalizedValue,
            effect,
            tolerationSeconds);
    }
}

public static class ComputeProfileFactory
{
    public sealed record CreateOptions(string Id, DateTime CreatedAtUtc);

    public static ComputeProfile Create(
        string displayName,
        string description,
        ComputeProfileResourceRequirements resources,
        ComputeArchitecture architecture,
        IEnumerable<ComputeProfileExtendedResource> extendedResources,
        IEnumerable<ResourceTypeId> allowedResourceTypes,
        ComputeProfileTenantAccess tenantAccess,
        IEnumerable<ComputeProfileTargetMapping> targetMappings,
        bool isEnabled,
        bool isDefault,
        CreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var values = Normalize(
            displayName,
            description,
            resources,
            architecture,
            extendedResources,
            allowedResourceTypes,
            tenantAccess,
            targetMappings,
            isEnabled,
            isDefault);
        var createdAtUtc = ComputeProfileValueGuard.Utc(
            options.CreatedAtUtc,
            nameof(options.CreatedAtUtc));

        return new ComputeProfile(
            ComputeProfileValueGuard.ObjectId(options.Id, nameof(options.Id)),
            values.DisplayName,
            values.Description,
            values.Resources,
            values.Architecture,
            values.ExtendedResources,
            values.AllowedResourceTypes,
            values.TenantAccess,
            values.TargetMappings,
            values.IsEnabled,
            values.IsDefault,
            Revision: 1,
            ResolutionFence: 0,
            createdAtUtc,
            createdAtUtc);
    }

    internal static ComputeProfileValues Normalize(
        string displayName,
        string description,
        ComputeProfileResourceRequirements resources,
        ComputeArchitecture architecture,
        IEnumerable<ComputeProfileExtendedResource> extendedResources,
        IEnumerable<ResourceTypeId> allowedResourceTypes,
        ComputeProfileTenantAccess tenantAccess,
        IEnumerable<ComputeProfileTargetMapping> targetMappings,
        bool isEnabled,
        bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(extendedResources);
        ArgumentNullException.ThrowIfNull(allowedResourceTypes);
        ArgumentNullException.ThrowIfNull(tenantAccess);
        ArgumentNullException.ThrowIfNull(targetMappings);
        if (!Enum.IsDefined(architecture))
            throw new ArgumentOutOfRangeException(nameof(architecture));
        if (isDefault && !isEnabled)
            throw new ArgumentException("The default compute profile must be enabled.", nameof(isEnabled));

        var normalizedResources = ComputeProfileResourceRequirements.Create(
            resources.Requests,
            resources.Limits);
        var normalizedExtendedResources = extendedResources
            .Select(resource => ComputeProfileExtendedResource.Create(
                resource.Name,
                resource.Quantity))
            .OrderBy(resource => resource.Name, StringComparer.Ordinal)
            .ToImmutableList();
        if (normalizedExtendedResources.Select(resource => resource.Name)
            .Distinct(StringComparer.Ordinal).Count() != normalizedExtendedResources.Count)
        {
            throw new ArgumentException(
                "Extended resource names must be unique.",
                nameof(extendedResources));
        }

        var normalizedResourceTypes = allowedResourceTypes
            .Select(resourceType => ResourceTypeId.Create(resourceType.Value).Value)
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (normalizedResourceTypes.Count == 0)
            throw new ArgumentException("At least one resource type is required.", nameof(allowedResourceTypes));

        ComputeProfileTenantAccess normalizedTenantAccess = tenantAccess switch
        {
            AllTenantsComputeProfileAccess => AllTenantsComputeProfileAccess.Create(),
            SelectedTenantsComputeProfileAccess selected =>
                SelectedTenantsComputeProfileAccess.Create(selected.TenantIds),
            _ => throw new ArgumentOutOfRangeException(nameof(tenantAccess))
        };
        var normalizedMappings = targetMappings
            .Select(mapping => ComputeProfileTargetMapping.Create(
                mapping.DeploymentTargetId,
                mapping.NodeSelector,
                mapping.RequiredNodeAffinity,
                mapping.PreferredNodeAffinity,
                mapping.Tolerations,
                mapping.RuntimeClassName))
            .OrderBy(mapping => mapping.DeploymentTargetId, StringComparer.Ordinal)
            .ToImmutableList();
        if (normalizedMappings.Count == 0)
            throw new ArgumentException("At least one target mapping is required.", nameof(targetMappings));
        if (normalizedMappings.Select(mapping => mapping.DeploymentTargetId)
            .Distinct(StringComparer.Ordinal).Count() != normalizedMappings.Count)
        {
            throw new ArgumentException(
                "Deployment target mappings must be unique.",
                nameof(targetMappings));
        }

        return new ComputeProfileValues(
            ComputeProfileValueGuard.Required(
                displayName,
                nameof(displayName),
                ComputeProfileValueGuard.MaximumDisplayNameLength),
            ComputeProfileValueGuard.Optional(
                description,
                nameof(description),
                ComputeProfileValueGuard.MaximumDescriptionLength),
            normalizedResources,
            architecture,
            normalizedExtendedResources,
            normalizedResourceTypes,
            normalizedTenantAccess,
            normalizedMappings,
            isEnabled,
            isDefault);
    }
}

internal sealed record ComputeProfileValues(
    string DisplayName,
    string Description,
    ComputeProfileResourceRequirements Resources,
    ComputeArchitecture Architecture,
    ImmutableList<ComputeProfileExtendedResource> ExtendedResources,
    ImmutableHashSet<string> AllowedResourceTypes,
    ComputeProfileTenantAccess TenantAccess,
    ImmutableList<ComputeProfileTargetMapping> TargetMappings,
    bool IsEnabled,
    bool IsDefault);

internal static partial class ComputeProfileValueGuard
{
    public const int MaximumDisplayNameLength = 80;
    public const int MaximumDescriptionLength = 500;
    private const int MaximumLabelNameLength = 63;
    private const int MaximumDnsSubdomainLength = 253;

    public static long Positive(long value, string parameterName) =>
        value > 0
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "The value must be positive.");

    public static string ObjectId(string? value, string parameterName) =>
        !string.IsNullOrWhiteSpace(value) && MongoDB.Bson.ObjectId.TryParse(value.Trim(), out _)
            ? value.Trim()
            : throw new ArgumentException("The value must be a MongoDB ObjectId.", parameterName);

    public static DateTime Utc(DateTime value, string parameterName) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : throw new ArgumentException("The value must be UTC.", parameterName);

    public static string Required(
        string? value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value is required.", parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
            throw new ArgumentException($"The value must be {maximumLength} characters or less.", parameterName);
        return normalized;
    }

    public static string Optional(
        string? value,
        string parameterName,
        int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximumLength)
            throw new ArgumentException($"The value must be {maximumLength} characters or less.", parameterName);
        return normalized;
    }

    public static string ExtendedResourceName(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, MaximumDnsSubdomainLength + MaximumLabelNameLength + 1);
        var separator = normalized.IndexOf('/');
        if (separator <= 0 ||
            separator != normalized.LastIndexOf('/') ||
            !IsDnsSubdomain(normalized[..separator]) ||
            !IsLabelName(normalized[(separator + 1)..]) ||
            string.Equals(normalized[..separator], "kubernetes.io", StringComparison.Ordinal) ||
            normalized[..separator].EndsWith(".kubernetes.io", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The value must be a non-Kubernetes qualified resource name.",
                parameterName);
        }

        return normalized;
    }

    public static string LabelKey(string? value, string parameterName)
    {
        var normalized = Required(
            value,
            parameterName,
            MaximumDnsSubdomainLength + MaximumLabelNameLength + 1);
        var separator = normalized.IndexOf('/');
        var valid = separator < 0
            ? IsLabelName(normalized)
            : separator == normalized.LastIndexOf('/') &&
              IsDnsSubdomain(normalized[..separator]) &&
              IsLabelName(normalized[(separator + 1)..]);
        return valid
            ? normalized
            : throw new ArgumentException("The value must be a Kubernetes label key.", parameterName);
    }

    public static string LabelValue(string? value, string parameterName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= MaximumLabelNameLength &&
               (normalized.Length == 0 || IsLabelName(normalized))
            ? normalized
            : throw new ArgumentException("The value must be a Kubernetes label value.", parameterName);
    }

    public static string? OptionalDnsSubdomain(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return IsDnsSubdomain(normalized)
            ? normalized
            : throw new ArgumentException("The value must be a Kubernetes DNS subdomain.", parameterName);
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
