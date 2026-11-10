using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.StorageProfiles.Primitives;
using Pole.Workloads;

namespace Pole.Features.StorageProfiles.Domain;

public sealed record StorageProfile(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TechnicalName,
    string DisplayName,
    string Description,
    StorageMediaType MediaType,
    StoragePerformanceTier PerformanceTier,
    bool IsEnabled,
    bool IsDefault,
    ImmutableList<StorageProfileTargetMapping> TargetMappings,
    ImmutableHashSet<string> AllowedResourceTypes,
    StorageProfileTenantAccess TenantAccess,
    StorageProfileSizeLimits? SizeLimits,
    long Revision,
    long ResolutionFence,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
)
{
    public StorageProfile Reconfigure(
        string displayName,
        string description,
        StorageMediaType mediaType,
        StoragePerformanceTier performanceTier,
        bool isEnabled,
        bool isDefault,
        IEnumerable<StorageProfileTargetMapping> targetMappings,
        IEnumerable<ResourceTypeId> allowedResourceTypes,
        StorageProfileTenantAccess tenantAccess,
        StorageProfileSizeLimits? sizeLimits,
        DateTime updatedAtUtc)
    {
        var values = StorageProfileFactory.Normalize(
            displayName,
            description,
            mediaType,
            performanceTier,
            isEnabled,
            isDefault,
            targetMappings,
            allowedResourceTypes,
            tenantAccess,
            sizeLimits);
        if (HasSameConfiguration(values))
            return this;

        var normalizedUpdatedAtUtc = StorageProfileValueGuard.Utc(updatedAtUtc, nameof(updatedAtUtc));
        if (normalizedUpdatedAtUtc < UpdatedAtUtc)
            throw new ArgumentException("The update time cannot move backwards.", nameof(updatedAtUtc));

        return new StorageProfile(
            Id,
            TechnicalName,
            values.DisplayName,
            values.Description,
            values.MediaType,
            values.PerformanceTier,
            values.IsEnabled,
            values.IsDefault,
            values.TargetMappings,
            values.AllowedResourceTypes,
            values.TenantAccess,
            values.SizeLimits,
            checked(StorageProfileValueGuard.Positive(Revision, nameof(Revision)) + 1),
            ResolutionFence,
            CreatedAtUtc,
            normalizedUpdatedAtUtc);
    }

    private bool HasSameConfiguration(StorageProfileValues values) =>
        string.Equals(DisplayName, values.DisplayName, StringComparison.Ordinal) &&
        string.Equals(Description, values.Description, StringComparison.Ordinal) && MediaType == values.MediaType &&
        PerformanceTier == values.PerformanceTier && IsEnabled == values.IsEnabled && IsDefault == values.IsDefault &&
        TargetMappings.SequenceEqual(values.TargetMappings) &&
        AllowedResourceTypes.SetEquals(values.AllowedResourceTypes) && TenantAccess.Mode == values.TenantAccess.Mode &&
        TenantAccess.TenantIds.SetEquals(values.TenantAccess.TenantIds) && Equals(SizeLimits, values.SizeLimits);
}

public sealed record StorageProfileTargetMapping(
    [property: BsonRepresentation(BsonType.ObjectId)]
    string DeploymentTargetId,
    string StorageClassName
)
{
    public const int MaximumStorageClassNameLength = 253;

    public static StorageProfileTargetMapping Create(string deploymentTargetId, string storageClassName) => new(
        StorageProfileValueGuard.ObjectId(deploymentTargetId, nameof(deploymentTargetId)),
        StorageProfileValueGuard.StorageClassName(storageClassName, nameof(storageClassName)));

    public static bool IsValidStorageClassName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        if (normalized.Length > MaximumStorageClassNameLength)
            return false;

        return normalized.Split('.').All(IsValidDnsLabel);
    }

    private static bool IsValidDnsLabel(string label)
    {
        if (label.Length is < 1 or > 63 || !IsLowerAlphaNumeric(label[0]) || !IsLowerAlphaNumeric(label[^1]))
            return false;

        return label.All(character => IsLowerAlphaNumeric(character) || character == '-');
    }

    private static bool IsLowerAlphaNumeric(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9';
}

public sealed record StorageProfileTenantAccess(
    StorageProfileTenantAccessMode Mode,
    ImmutableHashSet<string> TenantIds
)
{
    public static StorageProfileTenantAccess Create(StorageProfileTenantAccessMode mode, IEnumerable<string>? tenantIds)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), "Tenant access mode is invalid.");

        var normalizedTenantIds = mode == StorageProfileTenantAccessMode.AllTenants
            ? ImmutableHashSet<string>.Empty
            : (tenantIds ?? []).Select(id => StorageProfileValueGuard.ObjectId(id, nameof(tenantIds)))
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (mode == StorageProfileTenantAccessMode.SelectedTenants && normalizedTenantIds.Count == 0)
            throw new ArgumentException(
                "At least one tenant ID is required for selected-tenant access.",
                nameof(tenantIds));

        return new StorageProfileTenantAccess(mode, normalizedTenantIds);
    }
}

public sealed record StorageProfileSizeLimits(
    int? MinSizeGb,
    int? MaxSizeGb
)
{
    public static StorageProfileSizeLimits Create(int? minSizeGb, int? maxSizeGb)
    {
        if (minSizeGb is <= 0)
            throw new ArgumentOutOfRangeException(nameof(minSizeGb), "Minimum size must be positive.");
        if (maxSizeGb is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSizeGb), "Maximum size must be positive.");
        if (minSizeGb is not null && maxSizeGb is not null && minSizeGb > maxSizeGb)
            throw new ArgumentException("Minimum size must be less than or equal to maximum size.", nameof(minSizeGb));

        return new StorageProfileSizeLimits(minSizeGb, maxSizeGb);
    }
}

public static class StorageProfileFactory
{
    public static CreateOptions DefaultOptions => new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow);

    public static StorageProfile Create(
        string technicalName,
        string displayName,
        string description,
        StorageMediaType mediaType,
        StoragePerformanceTier performanceTier,
        bool isEnabled,
        bool isDefault,
        IEnumerable<StorageProfileTargetMapping> targetMappings,
        IEnumerable<ResourceTypeId> allowedResourceTypes,
        StorageProfileTenantAccess tenantAccess,
        StorageProfileSizeLimits? sizeLimits,
        CreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var values = Normalize(
            displayName,
            description,
            mediaType,
            performanceTier,
            isEnabled,
            isDefault,
            targetMappings,
            allowedResourceTypes,
            tenantAccess,
            sizeLimits);
        var id = StorageProfileValueGuard.ObjectId(options.Id, nameof(options.Id));
        var createdAtUtc = StorageProfileValueGuard.Utc(options.CreatedAtUtc, nameof(options.CreatedAtUtc));

        return new StorageProfile(
            id,
            ResourceTechnicalName.Require(technicalName),
            values.DisplayName,
            values.Description,
            values.MediaType,
            values.PerformanceTier,
            values.IsEnabled,
            values.IsDefault,
            values.TargetMappings,
            values.AllowedResourceTypes,
            values.TenantAccess,
            values.SizeLimits,
            Revision: 1,
            ResolutionFence: 0,
            createdAtUtc,
            createdAtUtc);
    }

    internal static StorageProfileValues Normalize(
        string displayName,
        string description,
        StorageMediaType mediaType,
        StoragePerformanceTier performanceTier,
        bool isEnabled,
        bool isDefault,
        IEnumerable<StorageProfileTargetMapping> targetMappings,
        IEnumerable<ResourceTypeId> allowedResourceTypes,
        StorageProfileTenantAccess tenantAccess,
        StorageProfileSizeLimits? sizeLimits)
    {
        ArgumentNullException.ThrowIfNull(targetMappings);
        ArgumentNullException.ThrowIfNull(allowedResourceTypes);
        ArgumentNullException.ThrowIfNull(tenantAccess);

        var normalizedDisplayName = StorageProfileValueGuard.Required(
            displayName,
            nameof(displayName),
            maximumLength: 80);
        var normalizedDescription = StorageProfileValueGuard.Optional(
            description,
            nameof(description),
            maximumLength: 500);
        if (!Enum.IsDefined(mediaType))
            throw new ArgumentOutOfRangeException(nameof(mediaType), "Storage media type is invalid.");
        if (!Enum.IsDefined(performanceTier))
            throw new ArgumentOutOfRangeException(nameof(performanceTier), "Storage performance tier is invalid.");
        if (isDefault && !isEnabled)
            throw new ArgumentException("The default storage profile must be enabled.", nameof(isDefault));

        var normalizedTargetMappings = NormalizeTargetMappings(targetMappings);
        if (normalizedTargetMappings.Count == 0)
            throw new ArgumentException("At least one target mapping is required.", nameof(targetMappings));

        var normalizedResourceTypes = allowedResourceTypes
            .Select(resourceType => ResourceTypeId.Create(resourceType.Value).Value)
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (normalizedResourceTypes.Count == 0)
            throw new ArgumentException(
                "At least one allowed resource type is required.",
                nameof(allowedResourceTypes));
        var normalizedTenantAccess = StorageProfileTenantAccess.Create(tenantAccess.Mode, tenantAccess.TenantIds);
        var normalizedSizeLimits = sizeLimits is null
            ? null
            : StorageProfileSizeLimits.Create(sizeLimits.MinSizeGb, sizeLimits.MaxSizeGb);

        return new StorageProfileValues(
            normalizedDisplayName,
            normalizedDescription,
            mediaType,
            performanceTier,
            isEnabled,
            isDefault,
            normalizedTargetMappings,
            normalizedResourceTypes,
            normalizedTenantAccess,
            normalizedSizeLimits);
    }

    public static ImmutableList<StorageProfileTargetMapping> NormalizeTargetMappings(
        IEnumerable<StorageProfileTargetMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var normalized = mappings.Select(mapping =>
            {
                ArgumentNullException.ThrowIfNull(mapping);
                return StorageProfileTargetMapping.Create(mapping.DeploymentTargetId, mapping.StorageClassName);
            })
            .OrderBy(mapping => mapping.DeploymentTargetId, StringComparer.Ordinal)
            .ToImmutableList();
        if (normalized.GroupBy(mapping => mapping.DeploymentTargetId, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
            throw new ArgumentException(
                "A storage profile can contain only one mapping per deployment target.",
                nameof(mappings));

        return normalized;
    }

    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc
    );
}

internal sealed record StorageProfileValues(
    string DisplayName,
    string Description,
    StorageMediaType MediaType,
    StoragePerformanceTier PerformanceTier,
    bool IsEnabled,
    bool IsDefault,
    ImmutableList<StorageProfileTargetMapping> TargetMappings,
    ImmutableHashSet<string> AllowedResourceTypes,
    StorageProfileTenantAccess TenantAccess,
    StorageProfileSizeLimits? SizeLimits
);

internal static class StorageProfileValueGuard
{
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
        var normalized = value?.Trim() ?? "";
        if (normalized.Length > maximumLength)
            throw new ArgumentException($"The value must be {maximumLength} characters or less.", parameterName);
        return normalized;
    }

    public static string ObjectId(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, maximumLength: 24);
        if (!MongoDB.Bson.ObjectId.TryParse(normalized, out _))
            throw new ArgumentException("The value must be a MongoDB ObjectId.", parameterName);
        return normalized;
    }

    public static string StorageClassName(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, StorageProfileTargetMapping.MaximumStorageClassNameLength);
        if (!StorageProfileTargetMapping.IsValidStorageClassName(normalized))
            throw new ArgumentException("The value must be a valid Kubernetes storage class name.", parameterName);
        return normalized;
    }

    public static long Positive(long value, string parameterName)
    {
        if (value < 1)
            throw new ArgumentOutOfRangeException(parameterName, "The value must be positive.");
        return value;
    }

    public static DateTime Utc(DateTime value, string parameterName)
    {
        if (value == default)
            throw new ArgumentException("The timestamp is required.", parameterName);
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }
}
