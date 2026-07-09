using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Domain;

public sealed record StorageProfile(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string DisplayName,
    string Description,
    StorageMediaType MediaType,
    StoragePerformanceTier PerformanceTier,
    bool IsEnabled,
    bool IsDefault,
    ImmutableList<string> StorageClassNames,
    ImmutableHashSet<ResourceType> AllowedResourceTypes,
    StorageProfileTenantAccess TenantAccess,
    StorageProfileSizeLimits? SizeLimits,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
);

public sealed record StorageProfileTenantAccess(
    StorageProfileTenantAccessMode Mode,
    ImmutableHashSet<string> TenantIds
);

public sealed record StorageProfileSizeLimits(
    int? MinSizeGb,
    int? MaxSizeGb
);

public static class StorageProfileFactory
{
    public sealed record StorageProfileCreateOptions(
        string Id,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc
    );

    public static StorageProfileCreateOptions DefaultCreateOptions =>
        new(
            ObjectId.GenerateNewId().ToString(),
            DateTime.UtcNow,
            DateTime.UtcNow);

    public static StorageProfile Create(
        string displayName,
        string description,
        StorageMediaType mediaType,
        StoragePerformanceTier performanceTier,
        bool isEnabled,
        bool isDefault,
        ImmutableList<string> storageClassNames,
        ImmutableHashSet<ResourceType> allowedResourceTypes,
        StorageProfileTenantAccess tenantAccess,
        StorageProfileSizeLimits? sizeLimits,
        StorageProfileCreateOptions options) =>
        new(
            options.Id,
            displayName,
            description,
            mediaType,
            performanceTier,
            isEnabled,
            isDefault,
            NormalizeStorageClassNames(storageClassNames),
            allowedResourceTypes,
            tenantAccess,
            sizeLimits,
            options.CreatedAtUtc,
            options.UpdatedAtUtc);

    public static ImmutableList<string> NormalizeStorageClassNames(IEnumerable<string> names) =>
        names
            .Select(name => name.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableList();
}
