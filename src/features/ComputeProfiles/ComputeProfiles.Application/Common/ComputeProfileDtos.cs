// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Common;

public sealed record ComputeResourceValuesDto(
    long CpuMillis,
    long MemoryMi
);

public sealed record ComputeResourceRequirementsDto(
    ComputeResourceValuesDto Requests,
    ComputeResourceValuesDto Limits
);

public sealed record ComputeExtendedResourceDto(
    string Name,
    long Quantity
);

public sealed record ComputeNodeSelectorDto(
    string Key,
    string Value
);

public sealed record ComputeNodeAffinityExpressionDto(
    string Key,
    string Operator,
    IReadOnlyCollection<string> Values
);

public sealed record ComputeNodeAffinityTermDto(
    IReadOnlyCollection<ComputeNodeAffinityExpressionDto> Expressions
);

public sealed record ComputePreferredNodeAffinityTermDto(
    int Weight,
    ComputeNodeAffinityTermDto Term
);

public sealed record ComputeTolerationDto(
    string? Key,
    string Operator,
    string? Value,
    string Effect,
    long? TolerationSeconds
);

public sealed record ComputeProfileTargetMappingDto(
    string DeploymentTargetId,
    IReadOnlyCollection<ComputeNodeSelectorDto> NodeSelector,
    IReadOnlyCollection<ComputeNodeAffinityTermDto> RequiredNodeAffinity,
    IReadOnlyCollection<ComputePreferredNodeAffinityTermDto> PreferredNodeAffinity,
    IReadOnlyCollection<ComputeTolerationDto> Tolerations,
    string? RuntimeClassName
);

public sealed record ComputeProfileTenantAccessDto(
    string Kind,
    IReadOnlyCollection<string> TenantIds
);

public sealed record ComputeProfileDto(
    string Id,
    string DisplayName,
    string Description,
    ComputeResourceRequirementsDto Resources,
    string Architecture,
    IReadOnlyCollection<ComputeExtendedResourceDto> ExtendedResources,
    IReadOnlyCollection<string> AllowedResourceTypes,
    ComputeProfileTenantAccessDto TenantAccess,
    IReadOnlyCollection<ComputeProfileTargetMappingDto> TargetMappings,
    bool IsEnabled,
    bool IsDefault,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string TechnicalName
);

public sealed record AvailableComputeProfileDto(
    string Id,
    string DisplayName,
    string Description,
    ComputeResourceRequirementsDto Resources,
    string Architecture,
    IReadOnlyCollection<ComputeExtendedResourceDto> ExtendedResources,
    bool IsDefault,
    long Revision,
    string TechnicalName
);

public sealed record ComputeProfilePageDto(
    IReadOnlyCollection<ComputeProfileDto> Items,
    int PageNumber,
    int PageSize,
    long TotalCount
);

public static class ComputeProfileDtoExtensions
{
    public static ComputeProfileDto ToDto(this ComputeProfile profile) => new(
        profile.Id,
        profile.DisplayName,
        profile.Description,
        profile.Resources.ToDto(),
        profile.Architecture.ToString(),
        profile.ExtendedResources.Select(ToDto).ToArray(),
        profile.AllowedResourceTypes.Order(StringComparer.Ordinal).ToArray(),
        profile.TenantAccess.ToDto(),
        profile.TargetMappings.Select(ToDto).ToArray(),
        profile.IsEnabled,
        profile.IsDefault,
        profile.Revision,
        profile.CreatedAtUtc,
        profile.UpdatedAtUtc,
        profile.TechnicalName);

    public static AvailableComputeProfileDto ToAvailableDto(this ComputeProfile profile) => new(
        profile.Id,
        profile.DisplayName,
        profile.Description,
        profile.Resources.ToDto(),
        profile.Architecture.ToString(),
        profile.ExtendedResources.Select(ToDto).ToArray(),
        profile.IsDefault,
        profile.Revision,
        profile.TechnicalName);

    private static ComputeResourceRequirementsDto ToDto(this ComputeProfileResourceRequirements resources) => new(
        new ComputeResourceValuesDto(resources.Requests.CpuMillis, resources.Requests.MemoryMi),
        new ComputeResourceValuesDto(resources.Limits.CpuMillis, resources.Limits.MemoryMi));

    private static ComputeExtendedResourceDto ToDto(ComputeProfileExtendedResource resource) =>
        new(resource.Name, resource.Quantity);

    private static ComputeProfileTenantAccessDto ToDto(this ComputeProfileTenantAccess access) => access switch
    {
        AllTenantsComputeProfileAccess => new ComputeProfileTenantAccessDto(
            ComputeProfileTenantAccessKinds.AllTenants,
            []),
        SelectedTenantsComputeProfileAccess selected => new ComputeProfileTenantAccessDto(
            ComputeProfileTenantAccessKinds.SelectedTenants,
            selected.TenantIds.Order(StringComparer.Ordinal).ToArray()),
        _ => throw new InvalidOperationException("The compute-profile tenant access kind is not supported."),
    };

    private static ComputeProfileTargetMappingDto ToDto(ComputeProfileTargetMapping mapping) => new(
        mapping.DeploymentTargetId,
        mapping.NodeSelector.Select(item => new ComputeNodeSelectorDto(item.Key, item.Value)).ToArray(),
        mapping.RequiredNodeAffinity.Select(ToDto).ToArray(),
        mapping.PreferredNodeAffinity
            .Select(term => new ComputePreferredNodeAffinityTermDto(term.Weight, ToDto(term.Term)))
            .ToArray(),
        mapping.Tolerations
            .Select(item => new ComputeTolerationDto(
                item.Key,
                item.Operator.ToString(),
                item.Value,
                item.Effect.ToString(),
                item.TolerationSeconds))
            .ToArray(),
        mapping.RuntimeClassName);

    private static ComputeNodeAffinityTermDto ToDto(ComputeProfileNodeAffinityTerm term) => new(
        term.Expressions
            .Select(expression => new ComputeNodeAffinityExpressionDto(
                expression.Key,
                expression.Operator.ToString(),
                expression.Values))
            .ToArray());
}
