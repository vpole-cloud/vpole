using FluentValidation;
using Pole.Validation;
using MongoDB.Bson;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.Workloads;

namespace Pole.Features.StorageProfiles.Application.Common;

public abstract record StorageProfileMutationRequestBody(
    string DisplayName,
    string Description,
    string MediaType,
    string PerformanceTier,
    bool IsEnabled,
    bool IsDefault,
    IReadOnlyCollection<StorageProfileTargetMappingRequestBody> TargetMappings,
    IReadOnlyCollection<string> AllowedResourceTypes,
    StorageProfileTenantAccessDto TenantAccess,
    StorageProfileSizeLimitsDto? SizeLimits);

public sealed record StorageProfileTargetMappingRequestBody(
    string DeploymentTargetId,
    string StorageClassName);

public abstract class StorageProfileMutationRequestBodyValidator<TBody>
    : AbstractValidator<TBody>
    where TBody : StorageProfileMutationRequestBody
{
    protected StorageProfileMutationRequestBodyValidator()
    {
        RuleFor(body => body.DisplayName)
            .NotEmpty()
            .MaximumLength(80);
        RuleFor(body => body.Description)
            .MaximumLength(500);
        RuleFor(body => body.MediaType)
            .Must(BeDefinedEnumValue<StorageMediaType>)
            .WithMessage("Media type is invalid.");
        RuleFor(body => body.PerformanceTier)
            .Must(BeDefinedEnumValue<StoragePerformanceTier>)
            .WithMessage("Performance tier is invalid.");
        RuleFor(body => body.IsDefault)
            .Equal(false)
            .When(body => !body.IsEnabled)
            .WithMessage("The default storage profile must be enabled.");

        RuleFor(body => body.TargetMappings)
            .NotNull()
            .NotEmpty()
            .Must(HaveUniqueDeploymentTargets)
            .WithMessage("A storage profile can contain only one mapping per deployment target.");
        RuleForEach(body => body.TargetMappings)
            .NotNull()
            .SetValidator(new StorageProfileTargetMappingRequestBodyValidator());

        RuleFor(body => body.AllowedResourceTypes)
            .NotNull()
            .NotEmpty();
        RuleForEach(body => body.AllowedResourceTypes)
            .Must(value => ResourceTypeId.TryCreate(value, out _))
            .WithMessage("Allowed resource type is invalid.");

        RuleFor(body => body.TenantAccess)
            .NotNull()
            .SetValidator(new StorageProfileTenantAccessDtoValidator());
        When(
            body => body.SizeLimits is not null,
            () => RuleFor(body => body.SizeLimits!)
                .SetValidator(new StorageProfileSizeLimitsDtoValidator()));
    }

    private static bool HaveUniqueDeploymentTargets(
        IReadOnlyCollection<StorageProfileTargetMappingRequestBody>? mappings) =>
        mappings is null ||
        mappings.All(mapping => mapping is not null) &&
        mappings
            .Select(mapping => mapping!.DeploymentTargetId?.Trim())
            .Distinct(StringComparer.Ordinal)
            .Count() == mappings.Count;

    private static bool BeDefinedEnumValue<TEnum>(string? value)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) &&
        Enum.IsDefined(parsed);
}

public sealed class StorageProfileTargetMappingRequestBodyValidator
    : AbstractValidator<StorageProfileTargetMappingRequestBody>
{
    public StorageProfileTargetMappingRequestBodyValidator()
    {
        RuleFor(mapping => mapping.DeploymentTargetId)
            .NotEmpty()
            .MongoObjectId("Deployment target ID");
        RuleFor(mapping => mapping.StorageClassName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(StorageProfileTargetMapping.MaximumStorageClassNameLength)
            .Must(StorageProfileTargetMapping.IsValidStorageClassName)
            .WithMessage("Storage class name must be a valid Kubernetes resource name.");
    }
}

public sealed class StorageProfileTenantAccessDtoValidator
    : AbstractValidator<StorageProfileTenantAccessDto>
{
    public StorageProfileTenantAccessDtoValidator()
    {
        RuleFor(access => access.Mode)
            .Must(mode =>
                Enum.TryParse<StorageProfileTenantAccessMode>(
                    mode,
                    ignoreCase: true,
                    out var parsed) &&
                Enum.IsDefined(parsed))
            .WithMessage("Tenant access mode is invalid.");
        RuleFor(access => access.TenantIds)
            .NotNull();
        RuleFor(access => access.TenantIds)
            .Must(ids => ids is not null && ids.Count > 0)
            .When(access => string.Equals(
                access.Mode,
                nameof(StorageProfileTenantAccessMode.SelectedTenants),
                StringComparison.OrdinalIgnoreCase))
            .WithMessage("At least one tenant ID is required for selected-tenant access.");
        RuleFor(access => access.TenantIds)
            .Empty()
            .When(access => string.Equals(
                access.Mode,
                nameof(StorageProfileTenantAccessMode.AllTenants),
                StringComparison.OrdinalIgnoreCase))
            .WithMessage("Tenant IDs must be empty when access is granted to all tenants.");
        RuleForEach(access => access.TenantIds)
            .MongoObjectId("Tenant ID");
    }
}

public sealed class StorageProfileSizeLimitsDtoValidator
    : AbstractValidator<StorageProfileSizeLimitsDto>
{
    public StorageProfileSizeLimitsDtoValidator()
    {
        RuleFor(limits => limits.MinSizeGb)
            .GreaterThan(0)
            .When(limits => limits.MinSizeGb is not null);
        RuleFor(limits => limits.MaxSizeGb)
            .GreaterThan(0)
            .When(limits => limits.MaxSizeGb is not null);
        RuleFor(limits => limits.MaxSizeGb)
            .GreaterThanOrEqualTo(limits => limits.MinSizeGb)
            .When(limits => limits.MinSizeGb is not null && limits.MaxSizeGb is not null)
            .WithMessage("Maximum size must be greater than or equal to minimum size.");
    }
}
