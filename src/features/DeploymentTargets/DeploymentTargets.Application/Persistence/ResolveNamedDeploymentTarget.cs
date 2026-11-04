using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

internal sealed class MongoResolveNamedDeploymentTarget(
    IMongoCollection<DeploymentTarget> targets,
    AdmitDeploymentTargetForNewWorkload admit)
{
    public async Task<ResolveNamedDeploymentTargetOutcome> InvokeAsync(NamedDeploymentTargetRequest request, CancellationToken ct = default)
    {
        // Targets are platform-shared. Tenant/resource context still goes through the owning
        // admission contract; names never grant access or replace admission.
        var candidates = await targets.Find(DeploymentTargetFilters.ByTechnicalName(request.Name)).Limit(2).ToListAsync(ct);
        if (candidates.Count == 0) return new NamedDeploymentTargetUnavailable(request.Name);
        if (candidates.Count != 1) return new NamedDeploymentTargetAmbiguous(request.Name);
        var outcome = await admit(new(request.TenantId, request.ResourceType, null, candidates[0].Id,
            DeploymentTargetRequiredCapabilities.None), ct);
        return outcome switch
        {
            DeploymentTargetLookupDto target => target,
            DeploymentTargetNewWorkloadAdmissionError => new NamedDeploymentTargetUnavailable(request.Name)
        };
    }
}
