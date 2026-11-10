using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Catalog;
using Pole.Features.ComputeProfiles.Application.Mongo;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Resolution;

internal sealed class MongoResolveComputeProfiles(
    IMongoCollection<ComputeProfile> computeProfiles,
    IMongoCollection<ComputeProfileRevision> revisions
)
{
    public async Task<ResolveComputeProfilesOutcome> InvokeAsync(
        ComputeProfileBatchResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureValid(request);

        var requestedProfileIds = request.Targets
            .Where(target => !string.IsNullOrWhiteSpace(target.ComputeProfileId))
            .Select(target => target.ComputeProfileId!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var filter = requestedProfileIds.Length == 0
            ? ComputeProfileFilters.Default()
            : ComputeProfileFilters.ByRequestedIdsOrDefault(requestedProfileIds);
        var candidates = await computeProfiles.Find(filter).ToListAsync(cancellationToken);
        var profilesById = candidates.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var defaultProfile = candidates.SingleOrDefault(profile => profile.IsDefault);

        var resolved = new List<ResolvedComputeProfile>(request.Targets.Count);
        foreach (var target in request.Targets)
        {
            var profile = string.IsNullOrWhiteSpace(target.ComputeProfileId)
                ? defaultProfile
                : profilesById.GetValueOrDefault(target.ComputeProfileId.Trim());
            if (profile is null)
                return string.IsNullOrWhiteSpace(target.ComputeProfileId)
                    ? new ComputeProfileResolutionError(new DefaultComputeProfileNotConfigured())
                    : new ComputeProfileResolutionError(new ComputeProfileNotFound(target.ComputeProfileId.Trim()));

            var availability = ValidateAvailability(
                profile,
                request.TenantId,
                request.ResourceType,
                target.DeploymentTargetId);
            if (availability is not null)
                return new ComputeProfileResolutionError(availability);

            if (target.Revision is not null && target.Revision != profile.Revision)
            {
                var historical = await revisions
                    .Find(ComputeProfileRevisionFilters.Identity(profile.Id, target.Revision.Value))
                    .FirstOrDefaultAsync(cancellationToken);
                if (historical is null || !historical.Snapshot.TargetMappings.Any(mapping =>
                        mapping.DeploymentTargetId == target.DeploymentTargetId))
                    return new ComputeProfileResolutionError(
                        new ComputeProfileUnavailable(profile.Id, ComputeProfileUnavailableReason.RevisionUnavailable));
                profile = historical.Snapshot;
            }

            var mapping = profile.TargetMappings.Single(mapping => string.Equals(
                mapping.DeploymentTargetId,
                target.DeploymentTargetId.Trim(),
                StringComparison.Ordinal));
            resolved.Add(ToResolved(profile, mapping));
        }

        return resolved;
    }

    internal static ComputeProfileUnavailable? ValidateAvailability(
        ComputeProfile profile,
        string tenantId,
        ResourceTypeId resourceType,
        string deploymentTargetId)
    {
        ComputeProfileUnavailableReason? reason = null;
        if (!profile.IsEnabled)
            reason = ComputeProfileUnavailableReason.Disabled;
        else if (!profile.AllowedResourceTypes.Contains(resourceType.Value))
            reason = ComputeProfileUnavailableReason.ResourceTypeNotAllowed;
        else if (!MongoLoadAvailableComputeProfiles.AllowsTenant(profile.TenantAccess, tenantId))
            reason = ComputeProfileUnavailableReason.TenantNotAllowed;
        else if (!profile.TargetMappings.Any(mapping => string.Equals(
                     mapping.DeploymentTargetId,
                     deploymentTargetId.Trim(),
                     StringComparison.Ordinal)))
            reason = ComputeProfileUnavailableReason.DeploymentTargetNotConfigured;

        return reason is null ? null : new ComputeProfileUnavailable(profile.Id, reason.Value);
    }

    internal static ResolvedComputeProfile ToResolved(ComputeProfile profile, ComputeProfileTargetMapping mapping) =>
        new(
            profile.Id,
            profile.Revision,
            mapping.DeploymentTargetId,
            profile.DisplayName,
            new ComputeResourceValues(profile.Resources.Requests.CpuMillis, profile.Resources.Requests.MemoryMi),
            new ComputeResourceValues(profile.Resources.Limits.CpuMillis, profile.Resources.Limits.MemoryMi),
            profile.Architecture,
            profile.ExtendedResources
                .Select(resource => new ComputeExtendedResourceValue(resource.Name, resource.Quantity))
                .ToImmutableList(),
            mapping.NodeSelector
                .Select(selector => new ComputeNodeSelectorValue(selector.Key, selector.Value))
                .ToImmutableList(),
            mapping.RequiredNodeAffinity.Select(ToValue).ToImmutableList(),
            mapping.PreferredNodeAffinity
                .Select(term => new ComputePreferredNodeAffinityTermValue(term.Weight, ToValue(term.Term)))
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

    internal static void EnsureValid(ComputeProfileBatchResolutionRequest request)
    {
        if (!ObjectId.TryParse(request.TenantId, out _))
            throw new ArgumentException("Tenant ID must be a MongoDB ObjectId.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ResourceType.Value))
            throw new ArgumentException("Resource type is required.", nameof(request));
        if (request.Targets.Count == 0)
            throw new ArgumentException("At least one deployment target is required.", nameof(request));

        foreach (var target in request.Targets)
        {
            if (target.Revision is < 1 ||
                (target.Revision is not null && string.IsNullOrWhiteSpace(target.ComputeProfileId)))
                throw new ArgumentException("Pinned profiles require ID and positive revision.", nameof(request));
            if (!ObjectId.TryParse(target.DeploymentTargetId?.Trim(), out _))
                throw new ArgumentException("Every deployment target ID must be a MongoDB ObjectId.", nameof(request));
            if (!string.IsNullOrWhiteSpace(target.ComputeProfileId) &&
                !ObjectId.TryParse(target.ComputeProfileId.Trim(), out _))
                throw new ArgumentException("Every compute profile ID must be a MongoDB ObjectId.", nameof(request));
        }

        if (request.Targets
                .Select(target => target.DeploymentTargetId?.Trim())
                .Distinct(StringComparer.Ordinal)
                .Count() != request.Targets.Count)
            throw new ArgumentException("Each deployment target may be resolved only once per batch.", nameof(request));
    }

    private static ComputeNodeAffinityTermValue ToValue(ComputeProfileNodeAffinityTerm term) => new(
        term.Expressions
            .Select(expression => new ComputeNodeAffinityExpressionValue(
                expression.Key,
                expression.Operator,
                expression.Values))
            .ToImmutableList());
}
