using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal sealed class MongoResolveComputeProfiles(
    IMongoCollection<ComputeProfile> computeProfiles)
{
    public async Task<Result<IReadOnlyList<ResolvedComputeProfile>>> InvokeAsync(
        ComputeProfileBatchResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = Validate(request);
        if (validation is not null)
            return validation;

        var requestedProfileIds = request.Targets
            .Where(target => !string.IsNullOrWhiteSpace(target.ComputeProfileId))
            .Select(target => target.ComputeProfileId!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var filter = requestedProfileIds.Length == 0
            ? ComputeProfileFilters.Default()
            : Builders<ComputeProfile>.Filter.Or(
                ComputeProfileFilters.ByIds(requestedProfileIds),
                ComputeProfileFilters.Default());
        var candidates = await computeProfiles
            .Find(filter)
            .ToListAsync(cancellationToken);
        var profilesById = candidates.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var defaultProfile = candidates.SingleOrDefault(profile => profile.IsDefault);

        var resolved = new List<ResolvedComputeProfile>(request.Targets.Count);
        foreach (var target in request.Targets)
        {
            var profile = string.IsNullOrWhiteSpace(target.ComputeProfileId)
                ? defaultProfile
                : profilesById.GetValueOrDefault(target.ComputeProfileId.Trim());
            if (profile is null)
            {
                return string.IsNullOrWhiteSpace(target.ComputeProfileId)
                    ? Failures.Conflict("The default compute profile is not configured.")
                    : Failures.NotFound(
                        "Compute profile not found.",
                        target.ComputeProfileId.Trim());
            }

            var availability = ValidateAvailability(
                profile,
                request.TenantId,
                request.ResourceType,
                target.DeploymentTargetId);
            if (availability is not null)
                return availability;

            var mapping = profile.TargetMappings.Single(mapping =>
                string.Equals(
                    mapping.DeploymentTargetId,
                    target.DeploymentTargetId.Trim(),
                    StringComparison.Ordinal));
            resolved.Add(ToResolved(profile, mapping));
        }

        return resolved;
    }

    internal static SemanticValidationFailure? ValidateAvailability(
        ComputeProfile profile,
        string tenantId,
        ResourceTypeId resourceType,
        string deploymentTargetId)
    {
        var errors = new Dictionary<string, string[]>();
        if (!profile.IsEnabled)
            errors["ComputeProfileId"] = ["Compute profile is disabled."];
        else if (!profile.AllowedResourceTypes.Contains(resourceType.Value))
        {
            errors["ComputeProfileId"] =
                ["Compute profile is not available for this resource type."];
        }
        else if (!MongoLoadAvailableComputeProfiles.AllowsTenant(
                     profile.TenantAccess,
                     tenantId))
        {
            errors["ComputeProfileId"] =
                ["Compute profile is not available to the active tenant."];
        }
        else if (!profile.TargetMappings.Any(mapping =>
                     string.Equals(
                         mapping.DeploymentTargetId,
                         deploymentTargetId.Trim(),
                         StringComparison.Ordinal)))
        {
            errors["ComputeProfileId"] =
                ["Compute profile is not configured for the selected deployment target."];
        }

        return errors.Count == 0
            ? null
            : Failures.SemanticValidation(errors);
    }

    internal static ResolvedComputeProfile ToResolved(
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

    private static ValidationFailure? Validate(
        ComputeProfileBatchResolutionRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ObjectId.TryParse(request.TenantId, out _))
            errors["TenantId"] = ["Tenant ID must be a MongoDB ObjectId."];
        if (string.IsNullOrWhiteSpace(request.ResourceType.Value))
            errors["ResourceType"] = ["Resource type is required."];
        if (request.Targets.Count == 0)
            errors["Targets"] = ["At least one deployment target is required."];

        foreach (var target in request.Targets)
        {
            if (!ObjectId.TryParse(target.DeploymentTargetId?.Trim(), out _))
            {
                errors["Targets"] =
                    ["Every deployment target ID must be a MongoDB ObjectId."];
            }
            if (!string.IsNullOrWhiteSpace(target.ComputeProfileId) &&
                !ObjectId.TryParse(target.ComputeProfileId.Trim(), out _))
            {
                errors["Targets"] =
                    ["Every compute profile ID must be a MongoDB ObjectId."];
            }
        }

        if (request.Targets
            .Select(target => target.DeploymentTargetId?.Trim())
            .Distinct(StringComparer.Ordinal)
            .Count() != request.Targets.Count)
        {
            errors["Targets"] =
                ["Each deployment target may be resolved only once per batch."];
        }

        return errors.Count == 0
            ? null
            : Failures.ValidationFailure(errors);
    }

    private static ComputeNodeAffinityTermValue ToValue(
        ComputeProfileNodeAffinityTerm term) =>
        new(term.Expressions
            .Select(expression => new ComputeNodeAffinityExpressionValue(
                expression.Key,
                expression.Operator,
                expression.Values))
            .ToImmutableList());
}
