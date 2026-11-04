using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Persistence.Resolution;

internal sealed class MongoResolveNamedComputeProfile(
    IMongoCollection<ComputeProfile> profiles,
    ResolveComputeProfiles resolve)
{
    public async Task<ResolveNamedComputeProfileOutcome> InvokeAsync(NamedComputeProfileRequest request,
        CancellationToken ct = default)
    {
        var candidates = await profiles.Find(ComputeProfileFilters.NamedForWorkload(request.Name, request.TenantId,
            request.DeploymentTargetId, request.ResourceType))
            .Limit(2).ToListAsync(ct);
        if (candidates.Count == 0) return new NamedComputeProfileUnavailable(request.Name);
        if (candidates.Count != 1) return new NamedComputeProfileAmbiguous(request.Name);
        var outcome = await resolve(new(request.TenantId, request.ResourceType,
            [new(request.DeploymentTargetId, candidates[0].Id, request.Revision)]), ct);
        return outcome switch
        {
            IReadOnlyList<ResolvedComputeProfile> values => values.Single(),
            ComputeProfileResolutionError error => error switch
            {
                ComputeProfileUnavailable unavailable => unavailable,
                ComputeProfileNotFound => new NamedComputeProfileUnavailable(request.Name),
                DefaultComputeProfileNotConfigured => new NamedComputeProfileUnavailable(request.Name)
            }
        };
    }
}
