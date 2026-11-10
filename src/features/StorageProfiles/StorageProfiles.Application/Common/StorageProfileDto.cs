using System.Collections.Immutable;
using Pole.Features.StorageProfiles.Domain;
using Pole.Workloads;

namespace Pole.Features.StorageProfiles.Application.Common;

public sealed record StorageProfileDto(
    string Id,
    string TechnicalName,
    string DisplayName,
    string Description,
    string MediaType,
    string PerformanceTier,
    bool IsEnabled,
    bool IsDefault,
    IReadOnlyCollection<StorageProfileTargetMappingDto> TargetMappings,
    IReadOnlyCollection<string> AllowedResourceTypes,
    StorageProfileTenantAccessDto TenantAccess,
    StorageProfileSizeLimitsDto? SizeLimits,
    long Revision
);

public sealed record StorageProfileTargetMappingDto(
    string DeploymentTargetId,
    string StorageClassName
);

public sealed record StorageProfileTenantAccessDto(
    string Mode,
    IReadOnlyCollection<string> TenantIds
);

public sealed record StorageProfileSizeLimitsDto(
    int? MinSizeGb,
    int? MaxSizeGb
);

public static class StorageProfileDtoExtensions
{
    public static StorageProfileDto ToDto(this StorageProfile profile) => new(
        profile.Id,
        profile.TechnicalName,
        profile.DisplayName,
        profile.Description,
        profile.MediaType.ToString(),
        profile.PerformanceTier.ToString(),
        profile.IsEnabled,
        profile.IsDefault,
        profile.TargetMappings
            .Select(mapping => new StorageProfileTargetMappingDto(mapping.DeploymentTargetId, mapping.StorageClassName))
            .ToArray(),
        profile.AllowedResourceTypes.Order(StringComparer.Ordinal).ToImmutableArray(),
        profile.TenantAccess.ToDto(),
        profile.SizeLimits?.ToDto(),
        profile.Revision);

    public static StorageProfile ToDomain(
        this StorageProfileMutationRequestBody body,
        string technicalName,
        StorageProfileFactory.CreateOptions options) => StorageProfileFactory.Create(
        technicalName,
        body.DisplayName,
        body.Description,
        Enum.Parse<StorageMediaType>(body.MediaType, ignoreCase: true),
        Enum.Parse<StoragePerformanceTier>(body.PerformanceTier, ignoreCase: true),
        body.IsEnabled,
        body.IsDefault,
        body.TargetMappings.Select(mapping => mapping.ToDomain()),
        body.AllowedResourceTypes.Select(ResourceTypeId.Create),
        body.TenantAccess.ToDomain(),
        body.SizeLimits?.ToDomain(),
        options);

    public static StorageProfile Reconfigure(
        this StorageProfileMutationRequestBody body,
        StorageProfile profile,
        DateTime updatedAtUtc) => profile.Reconfigure(
        body.DisplayName,
        body.Description,
        Enum.Parse<StorageMediaType>(body.MediaType, ignoreCase: true),
        Enum.Parse<StoragePerformanceTier>(body.PerformanceTier, ignoreCase: true),
        body.IsEnabled,
        body.IsDefault,
        body.TargetMappings.Select(mapping => mapping.ToDomain()),
        body.AllowedResourceTypes.Select(ResourceTypeId.Create),
        body.TenantAccess.ToDomain(),
        body.SizeLimits?.ToDomain(),
        updatedAtUtc);

    internal static StorageProfileTargetMapping ToDomain(this StorageProfileTargetMappingRequestBody mapping) =>
        StorageProfileTargetMapping.Create(mapping.DeploymentTargetId, mapping.StorageClassName);

    private static StorageProfileTenantAccessDto ToDto(this StorageProfileTenantAccess access) => new(
        access.Mode.ToString(),
        access.TenantIds.Order(StringComparer.Ordinal).ToArray());

    private static StorageProfileSizeLimitsDto ToDto(this StorageProfileSizeLimits limits) =>
        new(limits.MinSizeGb, limits.MaxSizeGb);

    private static StorageProfileTenantAccess ToDomain(this StorageProfileTenantAccessDto access) =>
        StorageProfileTenantAccess.Create(
            Enum.Parse<StorageProfileTenantAccessMode>(access.Mode, ignoreCase: true),
            access.TenantIds);

    private static StorageProfileSizeLimits ToDomain(this StorageProfileSizeLimitsDto limits) =>
        StorageProfileSizeLimits.Create(limits.MinSizeGb, limits.MaxSizeGb);
}
