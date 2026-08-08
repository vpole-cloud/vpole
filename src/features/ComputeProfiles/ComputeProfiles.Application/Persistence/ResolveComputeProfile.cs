using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

public delegate Task<Result<ResolvedComputeProfile>> ResolveComputeProfile(
    ComputeProfileResolutionRequest request,
    CancellationToken cancellationToken = default);

internal sealed class MongoResolveComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles)
{
    public async Task<Result<ResolvedComputeProfile>> InvokeAsync(
        ComputeProfileResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = Validate(request);
        if (validation is not null)
            return validation;

        var profile = string.IsNullOrWhiteSpace(request.ComputeProfileId)
            ? await computeProfiles
                .Find(ComputeProfileFilters.Default())
                .FirstOrDefaultAsync(cancellationToken)
            : await computeProfiles
                .Find(ComputeProfileFilters.ById(request.ComputeProfileId.Trim()))
                .FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            return string.IsNullOrWhiteSpace(request.ComputeProfileId)
                ? Failures.Conflict("The default compute profile is not configured.")
                : Failures.NotFound(
                    "Compute profile not found.",
                    request.ComputeProfileId.Trim());
        }

        var availability = ValidateAvailability(profile, request);
        if (availability is not null)
            return availability;

        var mapping = profile.TargetMappings.Single(mapping =>
            string.Equals(
                mapping.DeploymentTargetId,
                request.DeploymentTargetId,
                StringComparison.Ordinal));
        return ToResolved(profile, mapping);
    }

    private static ValidationFailure? Validate(
        ComputeProfileResolutionRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!string.IsNullOrWhiteSpace(request.ComputeProfileId) &&
            !ObjectId.TryParse(request.ComputeProfileId.Trim(), out _))
        {
            errors["ComputeProfileId"] =
                ["Compute profile ID must be a MongoDB ObjectId."];
        }
        if (!ObjectId.TryParse(request.DeploymentTargetId, out _))
        {
            errors["DeploymentTargetId"] =
                ["Deployment target ID must be a MongoDB ObjectId."];
        }
        if (!ObjectId.TryParse(request.TenantId, out _))
            errors["TenantId"] = ["Tenant ID must be a MongoDB ObjectId."];
        if (string.IsNullOrWhiteSpace(request.ResourceType.Value))
            errors["ResourceType"] = ["Resource type is required."];
        return errors.Count == 0
            ? null
            : Failures.ValidationFailure(errors);
    }

    private static SemanticValidationFailure? ValidateAvailability(
        ComputeProfile profile,
        ComputeProfileResolutionRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!profile.IsEnabled)
            errors["ComputeProfileId"] = ["Compute profile is disabled."];
        else if (!profile.AllowedResourceTypes.Contains(request.ResourceType.Value))
        {
            errors["ComputeProfileId"] =
                ["Compute profile is not available for this resource type."];
        }
        else if (!MongoLoadAvailableComputeProfiles.AllowsTenant(
                     profile.TenantAccess,
                     request.TenantId))
        {
            errors["ComputeProfileId"] =
                ["Compute profile is not available to the active tenant."];
        }
        else if (!profile.TargetMappings.Any(mapping =>
                     string.Equals(
                         mapping.DeploymentTargetId,
                         request.DeploymentTargetId,
                         StringComparison.Ordinal)))
        {
            errors["ComputeProfileId"] =
                ["Compute profile is not configured for the selected deployment target."];
        }

        return errors.Count == 0
            ? null
            : Failures.SemanticValidation(errors);
    }

    private static ResolvedComputeProfile ToResolved(
        ComputeProfile profile,
        ComputeProfileTargetMapping mapping) =>
        new(
            profile.Id,
            profile.Revision,
            mapping.DeploymentTargetId,
            profile.DisplayName,
            new ComputeResourceValues(
                profile.Resources.Requests.CpuMillis,
                profile.Resources.Requests.MemoryMi),
            new ComputeResourceValues(
                profile.Resources.Limits.CpuMillis,
                profile.Resources.Limits.MemoryMi),
            profile.Architecture,
            profile.ExtendedResources
                .Select(resource => new ComputeExtendedResourceValue(
                    resource.Name,
                    resource.Quantity))
                .ToImmutableList(),
            mapping.NodeSelector
                .Select(selector => new ComputeNodeSelectorValue(
                    selector.Key,
                    selector.Value))
                .ToImmutableList(),
            mapping.RequiredNodeAffinity
                .Select(ToValue)
                .ToImmutableList(),
            mapping.PreferredNodeAffinity
                .Select(term => new ComputePreferredNodeAffinityTermValue(
                    term.Weight,
                    ToValue(term.Term)))
                .ToImmutableList(),
            mapping.Tolerations
                .Select(toleration => new ComputeTolerationValue(
                    toleration.Key,
                    toleration.Operator,
                    toleration.Value,
                    toleration.Effect,
                    toleration.TolerationSeconds))
                .ToImmutableList(),
            mapping.RuntimeClassName);

    private static ComputeNodeAffinityTermValue ToValue(
        ComputeProfileNodeAffinityTerm term) =>
        new(term.Expressions
            .Select(expression => new ComputeNodeAffinityExpressionValue(
                expression.Key,
                expression.Operator,
                expression.Values))
            .ToImmutableList());
}
