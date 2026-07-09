using System.Collections.Immutable;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Common;

public sealed record StorageProfileDto(
    string Id,
    string DisplayName,
    string Description,
    string MediaType,
    string PerformanceTier,
    bool IsEnabled,
    bool IsDefault,
    IReadOnlyCollection<string> StorageClassNames,
    IReadOnlyCollection<string> AllowedResourceTypes,
    StorageProfileTenantAccessDto TenantAccess,
    StorageProfileSizeLimitsDto? SizeLimits
);

public sealed record StorageProfileTenantAccessDto(
    string Mode,
    IReadOnlyCollection<string> TenantIds
);

public sealed record StorageProfileSizeLimitsDto(
    int? MinSizeGb,
    int? MaxSizeGb
);

public sealed record UpsertStorageProfileRequestBody(
    string DisplayName,
    string Description,
    string MediaType,
    string PerformanceTier,
    bool IsEnabled,
    bool IsDefault,
    IReadOnlyCollection<string> StorageClassNames,
    IReadOnlyCollection<string> AllowedResourceTypes,
    StorageProfileTenantAccessDto TenantAccess,
    StorageProfileSizeLimitsDto? SizeLimits
);

public static class StorageProfileDtoExtensions
{
    public static StorageProfileDto ToDto(this StorageProfile profile) =>
        new(
            profile.Id,
            profile.DisplayName,
            profile.Description,
            profile.MediaType.ToString(),
            profile.PerformanceTier.ToString(),
            profile.IsEnabled,
            profile.IsDefault,
            profile.StorageClassNames,
            profile.AllowedResourceTypes.Select(type => type.ToString()).ToImmutableList(),
            profile.TenantAccess.ToDto(),
            profile.SizeLimits?.ToDto());

    private static StorageProfileTenantAccessDto ToDto(this StorageProfileTenantAccess access) =>
        new(access.Mode.ToString(), access.TenantIds);

    private static StorageProfileSizeLimitsDto ToDto(this StorageProfileSizeLimits limits) =>
        new(limits.MinSizeGb, limits.MaxSizeGb);

    public static Result<StorageProfile> ToDomain(
        this UpsertStorageProfileRequestBody body,
        StorageProfileFactory.StorageProfileCreateOptions options)
    {
        var errors = ValidateBody(body);
        if (errors.Count > 0)
            return Failures.ValidationFailure(errors);

        var storageClasses = StorageProfileFactory.NormalizeStorageClassNames(body.StorageClassNames ?? []);
        var allowedResourceTypes = body.AllowedResourceTypes
            .Select(value => Enum.Parse<ResourceType>(value, ignoreCase: true))
            .ToImmutableHashSet();

        var tenantAccess = new StorageProfileTenantAccess(
            Enum.Parse<StorageProfileTenantAccessMode>(body.TenantAccess.Mode, ignoreCase: true),
            NormalizeTenantIds(body.TenantAccess.TenantIds));

        return StorageProfileFactory.Create(
            body.DisplayName.Trim(),
            (body.Description ?? string.Empty).Trim(),
            Enum.Parse<StorageMediaType>(body.MediaType, ignoreCase: true),
            Enum.Parse<StoragePerformanceTier>(body.PerformanceTier, ignoreCase: true),
            body.IsEnabled,
            body.IsDefault,
            storageClasses,
            allowedResourceTypes,
            tenantAccess,
            body.SizeLimits?.ToDomain(),
            options);
    }

    public static Dictionary<string, string[]> ValidateBody(UpsertStorageProfileRequestBody? body)
    {
        var errors = new Dictionary<string, string[]>();

        if (body is null)
        {
            errors["Body"] = ["Request body is required."];
            return errors;
        }

        if (string.IsNullOrWhiteSpace(body.DisplayName))
            errors["Body.DisplayName"] = ["Display name is required."];
        else if (body.DisplayName.Length > 80)
            errors["Body.DisplayName"] = ["Display name must be 80 characters or less."];

        if (body.Description is { Length: > 500 })
            errors["Body.Description"] = ["Description must be 500 characters or less."];

        if (!Enum.TryParse<StorageMediaType>(body.MediaType, ignoreCase: true, out _))
            errors["Body.MediaType"] = ["Media type is invalid."];

        if (!Enum.TryParse<StoragePerformanceTier>(body.PerformanceTier, ignoreCase: true, out _))
            errors["Body.PerformanceTier"] = ["Performance tier is invalid."];

        var storageClassNames = StorageProfileFactory.NormalizeStorageClassNames(body.StorageClassNames ?? []);
        if (storageClassNames.Count == 0)
            errors["Body.StorageClassNames"] = ["At least one storage class is required."];

        var resourceTypes = body.AllowedResourceTypes ?? [];
        if (resourceTypes.Count == 0)
        {
            errors["Body.AllowedResourceTypes"] = ["At least one allowed resource type is required."];
        }
        else
        {
            var invalidResourceTypes = resourceTypes
                .Where(value => !Enum.TryParse<ResourceType>(value, ignoreCase: true, out _))
                .ToArray();
            if (invalidResourceTypes.Length > 0)
                errors["Body.AllowedResourceTypes"] = [$"Invalid resource types: {string.Join(", ", invalidResourceTypes)}."];
        }

        if (body.TenantAccess is null)
        {
            errors["Body.TenantAccess"] = ["Tenant access is required."];
        }
        else if (!Enum.TryParse<StorageProfileTenantAccessMode>(body.TenantAccess.Mode, ignoreCase: true, out var accessMode))
        {
            errors["Body.TenantAccess.Mode"] = ["Tenant access mode is invalid."];
        }
        else if (accessMode == StorageProfileTenantAccessMode.SelectedTenants &&
                 NormalizeTenantIds(body.TenantAccess.TenantIds).Count == 0)
        {
            errors["Body.TenantAccess.TenantIds"] = ["At least one tenant id is required for selected-tenant access."];
        }

        if (body.SizeLimits is { MinSizeGb: not null } && body.SizeLimits.MinSizeGb <= 0)
            errors["Body.SizeLimits.MinSizeGb"] = ["Minimum size must be greater than zero."];

        if (body.SizeLimits is { MaxSizeGb: not null } && body.SizeLimits.MaxSizeGb <= 0)
            errors["Body.SizeLimits.MaxSizeGb"] = ["Maximum size must be greater than zero."];

        if (body.SizeLimits is { MinSizeGb: not null, MaxSizeGb: not null } &&
            body.SizeLimits.MinSizeGb > body.SizeLimits.MaxSizeGb)
            errors["Body.SizeLimits"] = ["Minimum size must be less than or equal to maximum size."];

        if (body.IsDefault && !body.IsEnabled)
            errors["Body.IsDefault"] = ["The default storage profile must be enabled."];

        return errors;
    }

    private static StorageProfileSizeLimits ToDomain(this StorageProfileSizeLimitsDto limits) =>
        new(limits.MinSizeGb, limits.MaxSizeGb);

    private static ImmutableHashSet<string> NormalizeTenantIds(IEnumerable<string>? tenantIds) =>
        (tenantIds ?? [])
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToImmutableHashSet();
}
