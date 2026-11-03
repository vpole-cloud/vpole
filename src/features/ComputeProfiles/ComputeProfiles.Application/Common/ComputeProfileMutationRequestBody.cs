using FluentValidation;
using MongoDB.Bson;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Validation;

namespace Pole.Features.ComputeProfiles.Application.Common;

public abstract record ComputeProfileMutationRequestBody(
    string DisplayName,
    string Description,
    ComputeResourceRequirementsRequestBody Resources,
    string Architecture,
    IReadOnlyCollection<ComputeExtendedResourceRequestBody> ExtendedResources,
    IReadOnlyCollection<string> AllowedResourceTypes,
    ComputeProfileTenantAccessRequestBody TenantAccess,
    IReadOnlyCollection<ComputeProfileTargetMappingRequestBody> TargetMappings,
    bool IsEnabled,
    bool IsDefault);

public sealed record ComputeResourceValuesRequestBody(
    long CpuMillis,
    long MemoryMi);

public sealed record ComputeResourceRequirementsRequestBody(
    ComputeResourceValuesRequestBody Requests,
    ComputeResourceValuesRequestBody Limits);

public sealed record ComputeExtendedResourceRequestBody(
    string Name,
    long Quantity);

public sealed record ComputeProfileTenantAccessRequestBody(
    string Kind,
    IReadOnlyCollection<string> TenantIds);

public sealed record ComputeProfileTargetMappingRequestBody(
    string DeploymentTargetId,
    IReadOnlyCollection<ComputeNodeSelectorRequestBody> NodeSelector,
    IReadOnlyCollection<ComputeNodeAffinityTermRequestBody> RequiredNodeAffinity,
    IReadOnlyCollection<ComputePreferredNodeAffinityTermRequestBody> PreferredNodeAffinity,
    IReadOnlyCollection<ComputeTolerationRequestBody> Tolerations,
    string? RuntimeClassName);

public sealed record ComputeNodeSelectorRequestBody(
    string Key,
    string Value);

public sealed record ComputeNodeAffinityExpressionRequestBody(
    string Key,
    string Operator,
    IReadOnlyCollection<string> Values);

public sealed record ComputeNodeAffinityTermRequestBody(
    IReadOnlyCollection<ComputeNodeAffinityExpressionRequestBody> Expressions);

public sealed record ComputePreferredNodeAffinityTermRequestBody(
    int Weight,
    ComputeNodeAffinityTermRequestBody Term);

public sealed record ComputeTolerationRequestBody(
    string? Key,
    string Operator,
    string? Value,
    string Effect,
    long? TolerationSeconds);

public abstract class ComputeProfileMutationRequestBodyValidator<TBody>
    : AbstractValidator<TBody>
    where TBody : ComputeProfileMutationRequestBody
{
    protected ComputeProfileMutationRequestBodyValidator()
    {
        RuleFor(body => body.DisplayName)
            .NotEmpty()
            .MaximumLength(80);
        RuleFor(body => body.Description)
            .NotNull()
            .MaximumLength(500);
        RuleFor(body => body.Architecture)
            .Must(ComputeProfileRequestParsing.IsDefined<ComputeArchitecture>)
            .WithMessage("Architecture is invalid.");
        RuleFor(body => body.IsDefault)
            .Equal(false)
            .When(body => !body.IsEnabled)
            .WithMessage("The default compute profile must be enabled.");

        RuleFor(body => body.Resources)
            .NotNull()
            .SetValidator(new ComputeResourceRequirementsRequestBodyValidator());

        RuleFor(body => body.ExtendedResources)
            .NotNull()
            .Must(ComputeProfileRequestValidation.HaveUniqueExtendedResourceNames)
            .WithMessage("Extended resource names must be unique.");
        RuleForEach(body => body.ExtendedResources)
            .NotNull()
            .SetValidator(new ComputeExtendedResourceRequestBodyValidator());

        RuleFor(body => body.AllowedResourceTypes)
            .NotNull()
            .NotEmpty()
            .Must(ComputeProfileRequestValidation.HaveUniqueValues)
            .WithMessage("Allowed resource types must be unique.");
        RuleForEach(body => body.AllowedResourceTypes)
            .Must(ComputeProfileRequestParsing.IsResourceType)
            .WithMessage("Allowed resource type is invalid.");

        RuleFor(body => body.TenantAccess)
            .NotNull()
            .SetValidator(new ComputeProfileTenantAccessRequestBodyValidator());

        RuleFor(body => body.TargetMappings)
            .NotNull()
            .NotEmpty()
            .Must(ComputeProfileRequestValidation.HaveUniqueDeploymentTargets)
            .WithMessage("A compute profile can contain only one mapping per deployment target.");
        RuleForEach(body => body.TargetMappings)
            .NotNull()
            .SetValidator(new ComputeProfileTargetMappingRequestBodyValidator());
    }
}

public sealed class ComputeResourceRequirementsRequestBodyValidator
    : AbstractValidator<ComputeResourceRequirementsRequestBody>
{
    public ComputeResourceRequirementsRequestBodyValidator()
    {
        RuleFor(resources => resources.Requests)
            .NotNull()
            .SetValidator(new ComputeResourceValuesRequestBodyValidator());
        RuleFor(resources => resources.Limits)
            .NotNull()
            .SetValidator(new ComputeResourceValuesRequestBodyValidator());
        RuleFor(resources => resources.Limits.CpuMillis)
            .GreaterThanOrEqualTo(resources => resources.Requests.CpuMillis)
            .When(resources => resources.Requests is not null && resources.Limits is not null)
            .WithMessage("CPU limit must be greater than or equal to the request.");
        RuleFor(resources => resources.Limits.MemoryMi)
            .GreaterThanOrEqualTo(resources => resources.Requests.MemoryMi)
            .When(resources => resources.Requests is not null && resources.Limits is not null)
            .WithMessage("Memory limit must be greater than or equal to the request.");
    }
}

public sealed class ComputeResourceValuesRequestBodyValidator
    : AbstractValidator<ComputeResourceValuesRequestBody>
{
    public ComputeResourceValuesRequestBodyValidator()
    {
        RuleFor(values => values.CpuMillis).GreaterThan(0);
        RuleFor(values => values.MemoryMi).GreaterThan(0);
    }
}

public sealed class ComputeExtendedResourceRequestBodyValidator
    : AbstractValidator<ComputeExtendedResourceRequestBody>
{
    public ComputeExtendedResourceRequestBodyValidator()
    {
        RuleFor(resource => resource.Name)
            .Must(ComputeProfileRequestParsing.IsExtendedResourceName)
            .WithMessage("Extended resource name must be a non-Kubernetes qualified resource name.");
        RuleFor(resource => resource.Quantity).GreaterThan(0);
    }
}

public sealed class ComputeProfileTenantAccessRequestBodyValidator
    : AbstractValidator<ComputeProfileTenantAccessRequestBody>
{
    public ComputeProfileTenantAccessRequestBodyValidator()
    {
        RuleFor(access => access.Kind)
            .Must(ComputeProfileRequestParsing.IsTenantAccessKind)
            .WithMessage("Tenant access kind is invalid.");
        RuleFor(access => access.TenantIds)
            .NotNull()
            .Must(ComputeProfileRequestValidation.HaveUniqueValues)
            .WithMessage("Tenant IDs must be unique.");
        RuleFor(access => access.TenantIds)
            .Empty()
            .When(access => string.Equals(
                access.Kind,
                ComputeProfileTenantAccessKinds.AllTenants,
                StringComparison.OrdinalIgnoreCase))
            .WithMessage("Tenant IDs must be empty when access is granted to all tenants.");
        RuleFor(access => access.TenantIds)
            .NotEmpty()
            .When(access => string.Equals(
                access.Kind,
                ComputeProfileTenantAccessKinds.SelectedTenants,
                StringComparison.OrdinalIgnoreCase))
            .WithMessage("At least one tenant ID is required for selected-tenant access.");
        RuleForEach(access => access.TenantIds)
            .MongoObjectId("Tenant ID");
    }
}

public sealed class ComputeProfileTargetMappingRequestBodyValidator
    : AbstractValidator<ComputeProfileTargetMappingRequestBody>
{
    public ComputeProfileTargetMappingRequestBodyValidator()
    {
        RuleFor(mapping => mapping.DeploymentTargetId)
            .MongoObjectId("Deployment target ID");
        RuleFor(mapping => mapping.RuntimeClassName)
            .Must(ComputeProfileRequestParsing.IsOptionalRuntimeClassName)
            .WithMessage("Runtime class name must be a Kubernetes DNS subdomain.");

        RuleFor(mapping => mapping.NodeSelector)
            .NotNull()
            .Must(ComputeProfileRequestValidation.HaveUniqueSelectorKeys)
            .WithMessage("Node selector keys must be unique.");
        RuleForEach(mapping => mapping.NodeSelector)
            .NotNull()
            .SetValidator(new ComputeNodeSelectorRequestBodyValidator());

        RuleFor(mapping => mapping.RequiredNodeAffinity).NotNull();
        RuleForEach(mapping => mapping.RequiredNodeAffinity)
            .NotNull()
            .SetValidator(new ComputeNodeAffinityTermRequestBodyValidator());

        RuleFor(mapping => mapping.PreferredNodeAffinity).NotNull();
        RuleForEach(mapping => mapping.PreferredNodeAffinity)
            .NotNull()
            .SetValidator(new ComputePreferredNodeAffinityTermRequestBodyValidator());

        RuleFor(mapping => mapping.Tolerations).NotNull();
        RuleForEach(mapping => mapping.Tolerations)
            .NotNull()
            .SetValidator(new ComputeTolerationRequestBodyValidator());
    }
}

public sealed class ComputeNodeSelectorRequestBodyValidator
    : AbstractValidator<ComputeNodeSelectorRequestBody>
{
    public ComputeNodeSelectorRequestBodyValidator() =>
        RuleFor(selector => selector)
            .Must(ComputeProfileRequestParsing.IsNodeSelector)
            .WithMessage("Node selector key or value is invalid.");
}

public sealed class ComputeNodeAffinityTermRequestBodyValidator
    : AbstractValidator<ComputeNodeAffinityTermRequestBody>
{
    public ComputeNodeAffinityTermRequestBodyValidator()
    {
        RuleFor(term => term.Expressions).NotNull().NotEmpty();
        RuleForEach(term => term.Expressions)
            .NotNull()
            .SetValidator(new ComputeNodeAffinityExpressionRequestBodyValidator());
    }
}

public sealed class ComputeNodeAffinityExpressionRequestBodyValidator
    : AbstractValidator<ComputeNodeAffinityExpressionRequestBody>
{
    public ComputeNodeAffinityExpressionRequestBodyValidator()
    {
        RuleFor(expression => expression.Operator)
            .Must(ComputeProfileRequestParsing.IsDefined<ComputeNodeAffinityOperator>)
            .WithMessage("Node affinity operator is invalid.");
        RuleFor(expression => expression.Values).NotNull();
        RuleFor(expression => expression)
            .Must(ComputeProfileRequestParsing.IsNodeAffinityExpression)
            .When(expression =>
                expression.Values is not null &&
                ComputeProfileRequestParsing.IsDefined<ComputeNodeAffinityOperator>(expression.Operator))
            .WithMessage("Node affinity expression is invalid.");
    }
}

public sealed class ComputePreferredNodeAffinityTermRequestBodyValidator
    : AbstractValidator<ComputePreferredNodeAffinityTermRequestBody>
{
    public ComputePreferredNodeAffinityTermRequestBodyValidator()
    {
        RuleFor(term => term.Weight).InclusiveBetween(1, 100);
        RuleFor(term => term.Term)
            .NotNull()
            .SetValidator(new ComputeNodeAffinityTermRequestBodyValidator());
    }
}

public sealed class ComputeTolerationRequestBodyValidator
    : AbstractValidator<ComputeTolerationRequestBody>
{
    public ComputeTolerationRequestBodyValidator()
    {
        RuleFor(toleration => toleration.Operator)
            .Must(ComputeProfileRequestParsing.IsDefined<ComputeTolerationOperator>)
            .WithMessage("Toleration operator is invalid.");
        RuleFor(toleration => toleration.Effect)
            .Must(ComputeProfileRequestParsing.IsDefined<ComputeTaintEffect>)
            .WithMessage("Toleration effect is invalid.");
        RuleFor(toleration => toleration)
            .Must(ComputeProfileRequestParsing.IsToleration)
            .When(toleration =>
                ComputeProfileRequestParsing.IsDefined<ComputeTolerationOperator>(toleration.Operator) &&
                ComputeProfileRequestParsing.IsDefined<ComputeTaintEffect>(toleration.Effect))
            .WithMessage("Toleration is invalid.");
    }
}

internal static class ComputeProfileRequestValidation
{
    public static bool HaveUniqueExtendedResourceNames(
        IReadOnlyCollection<ComputeExtendedResourceRequestBody>? resources) =>
        resources is null ||
        resources.All(resource => resource is not null) &&
        resources.Select(resource => resource.Name?.Trim())
            .Distinct(StringComparer.Ordinal)
            .Count() == resources.Count;

    public static bool HaveUniqueDeploymentTargets(
        IReadOnlyCollection<ComputeProfileTargetMappingRequestBody>? mappings) =>
        mappings is null ||
        mappings.All(mapping => mapping is not null) &&
        mappings.Select(mapping => mapping.DeploymentTargetId?.Trim())
            .Distinct(StringComparer.Ordinal)
            .Count() == mappings.Count;

    public static bool HaveUniqueSelectorKeys(
        IReadOnlyCollection<ComputeNodeSelectorRequestBody>? selectors) =>
        selectors is null ||
        selectors.All(selector => selector is not null) &&
        selectors.Select(selector => selector.Key?.Trim())
            .Distinct(StringComparer.Ordinal)
            .Count() == selectors.Count;

    public static bool HaveUniqueValues(IReadOnlyCollection<string>? values) =>
        values is null ||
        values.All(value => value is not null) &&
        values.Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Count() == values.Count;
}

internal static class ComputeProfileRequestParsing
{
    private static readonly string ValidationObjectId = ObjectId.Empty.ToString();

    public static bool IsDefined<TEnum>(string? value)
        where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>().Contains(value?.Trim(), StringComparer.OrdinalIgnoreCase);

    public static TEnum Parse<TEnum>(string value)
        where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(value, ignoreCase: true);

    public static bool IsResourceType(string? value) =>
        CanCreate(() => ResourceTypeId.Create(value!));

    public static bool IsExtendedResourceName(string? value) =>
        CanCreate(() => ComputeProfileExtendedResource.Create(value!, 1));

    public static bool IsOptionalRuntimeClassName(string? value) =>
        CanCreate(() => ComputeProfileTargetMapping.Create(
            ValidationObjectId,
            runtimeClassName: value));

    public static bool IsNodeSelector(ComputeNodeSelectorRequestBody? selector) =>
        selector is not null &&
        CanCreate(() => ComputeProfileNodeSelector.Create(selector.Key, selector.Value));

    public static bool IsNodeAffinityExpression(
        ComputeNodeAffinityExpressionRequestBody? expression) =>
        expression is not null &&
        CanCreate(() => ComputeProfileNodeAffinityExpression.Create(
            expression.Key,
            Parse<ComputeNodeAffinityOperator>(expression.Operator),
            expression.Values));

    public static bool IsToleration(ComputeTolerationRequestBody? toleration) =>
        toleration is not null &&
        CanCreate(() => ComputeProfileToleration.Create(
            toleration.Key,
            Parse<ComputeTolerationOperator>(toleration.Operator),
            toleration.Value,
            Parse<ComputeTaintEffect>(toleration.Effect),
            toleration.TolerationSeconds));

    public static bool IsTenantAccessKind(string? value) =>
        string.Equals(value, ComputeProfileTenantAccessKinds.AllTenants, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, ComputeProfileTenantAccessKinds.SelectedTenants, StringComparison.OrdinalIgnoreCase);

    private static bool CanCreate(Action factory)
    {
        try
        {
            factory();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public static class ComputeProfileMutationRequestBodyExtensions
{
    public static ComputeProfile ToDomain(
        this ComputeProfileMutationRequestBody body,
        string technicalName,
        ComputeProfileFactory.CreateOptions options) =>
        ComputeProfileFactory.Create(
            technicalName,
            body.DisplayName,
            body.Description,
            body.Resources.ToDomain(),
            ComputeProfileRequestParsing.Parse<ComputeArchitecture>(body.Architecture),
            body.ExtendedResources.Select(ToDomain),
            body.AllowedResourceTypes.Select(ResourceTypeId.Create),
            body.TenantAccess.ToDomain(),
            body.TargetMappings.Select(ToDomain),
            body.IsEnabled,
            body.IsDefault,
            options);

    public static ComputeProfile Reconfigure(
        this ComputeProfileMutationRequestBody body,
        ComputeProfile profile,
        DateTime updatedAtUtc) =>
        profile.Reconfigure(
            body.DisplayName,
            body.Description,
            body.Resources.ToDomain(),
            ComputeProfileRequestParsing.Parse<ComputeArchitecture>(body.Architecture),
            body.ExtendedResources.Select(ToDomain),
            body.AllowedResourceTypes.Select(ResourceTypeId.Create),
            body.TenantAccess.ToDomain(),
            body.TargetMappings.Select(ToDomain),
            body.IsEnabled,
            body.IsDefault,
            updatedAtUtc);

    private static ComputeProfileResourceRequirements ToDomain(
        this ComputeResourceRequirementsRequestBody resources) =>
        ComputeProfileResourceRequirements.Create(
            ComputeProfileResourceQuantity.Create(
                resources.Requests.CpuMillis,
                resources.Requests.MemoryMi),
            ComputeProfileResourceQuantity.Create(
                resources.Limits.CpuMillis,
                resources.Limits.MemoryMi));

    private static ComputeProfileExtendedResource ToDomain(
        ComputeExtendedResourceRequestBody resource) =>
        ComputeProfileExtendedResource.Create(resource.Name, resource.Quantity);

    private static ComputeProfileTenantAccess ToDomain(
        this ComputeProfileTenantAccessRequestBody access) =>
        string.Equals(
            access.Kind,
            ComputeProfileTenantAccessKinds.AllTenants,
            StringComparison.OrdinalIgnoreCase)
            ? AllTenantsComputeProfileAccess.Create()
            : SelectedTenantsComputeProfileAccess.Create(access.TenantIds);

    private static ComputeProfileTargetMapping ToDomain(
        ComputeProfileTargetMappingRequestBody mapping) =>
        ComputeProfileTargetMapping.Create(
            mapping.DeploymentTargetId,
            mapping.NodeSelector.Select(selector =>
                ComputeProfileNodeSelector.Create(selector.Key, selector.Value)),
            mapping.RequiredNodeAffinity.Select(ToDomain),
            mapping.PreferredNodeAffinity.Select(term =>
                ComputeProfilePreferredNodeAffinityTerm.Create(
                    term.Weight,
                    ToDomain(term.Term))),
            mapping.Tolerations.Select(toleration =>
                ComputeProfileToleration.Create(
                    toleration.Key,
                    ComputeProfileRequestParsing.Parse<ComputeTolerationOperator>(toleration.Operator),
                    toleration.Value,
                    ComputeProfileRequestParsing.Parse<ComputeTaintEffect>(toleration.Effect),
                    toleration.TolerationSeconds)),
            mapping.RuntimeClassName);

    private static ComputeProfileNodeAffinityTerm ToDomain(
        ComputeNodeAffinityTermRequestBody term) =>
        ComputeProfileNodeAffinityTerm.Create(term.Expressions.Select(expression =>
            ComputeProfileNodeAffinityExpression.Create(
                expression.Key,
                ComputeProfileRequestParsing.Parse<ComputeNodeAffinityOperator>(expression.Operator),
                expression.Values)));
}
