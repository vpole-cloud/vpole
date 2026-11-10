using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;
using static Pole.Features.WebServices.Application.Declarations.WebServiceDeclarations;
using static Pole.Features.WebServices.Application.Declarations.WebServicePlanning;

namespace Pole.Features.WebServices.Application.Declarations;

internal sealed class WebServiceDeclarationFences(
    IMongoCollection<WebService> services,
    WebServicePlanningReader planningReader,
    FenceIsolationScopePlanningSnapshot fencePlanningScope,
    FenceResourceDeploymentPlanningSnapshot fencePlanningPlacements,
    FenceComputeProfilePlanningSnapshot fencePlanningCompute,
    FenceDeploymentTargetPlanningSnapshot fencePlanningTarget,
    FenceSourceBuildConfiguration fenceBuildConfiguration,
    FenceResourceConnectionReferencePlanning fencePlanningConnections,
    FenceConsumerSecretPlanningSnapshot fencePlanningSecrets
)
{
    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var loaded = await planningReader.ReadExecutionPlanningAsync(request.Operation, session, cancellationToken);
        if (loaded is DeclarationReadBlocked blocked)
            return blocked;
        var dependencies = (WebServicePlanningDependencies)loaded.Value;
        if (!DependencyVersions(dependencies).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();
        if (dependencies.Current is { } current)
        {
            if (!request.Expected.Documents.Contains(
                    new DeclarationDocumentVersion(VersionKey(current.Id), current.Revision)) ||
                !request.Expected.Documents.Contains(
                    new DeclarationDocumentVersion(
                        DeclarationVersionKeys.PlanningFence(Kind, current.Id),
                        current.PlanningFence)))
                return new DeclarationPreconditionsChanged();
            var write = await services.UpdateOneAsync(
                session,
                WebServiceFilters.AtPlanningVersion(current),
                Builders<WebService>.Update.Inc(item => item.PlanningFence, 1),
                cancellationToken: cancellationToken);
            if (write.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        if (dependencies.BuildConfiguration is { } build &&
            await fenceBuildConfiguration(build, session, cancellationToken) is not SourceBuildConfigurationFenced)
            return new DeclarationPreconditionsChanged();

        // Fence references before placements: the destination can share the workload registration.
        if (dependencies.Connections is { } connections)
        {
            var fenced = await fencePlanningConnections(connections, session, cancellationToken);
            if (fenced is ResourceConnectionReferencesUnavailable)
                return new DeclarationReadBlocked(new WebServiceConnectionReferenceRejected());
            if (fenced is ResourceConnectionReferencePlanningChanged)
                return new DeclarationPreconditionsChanged();
        }

        if (await fencePlanningScope(dependencies.Scope, session, cancellationToken) is
                IsolationScopePlanningFenceRejected ||
            await fencePlanningTarget(dependencies.Target, session, cancellationToken) is
                DeploymentTargetPlanningFenceRejected ||
            await fencePlanningCompute(dependencies.Compute, session, cancellationToken) is
                ComputeProfilePlanningFenceRejected)
            return new DeclarationPreconditionsChanged();
        if (dependencies.Placements is { } placements &&
            await fencePlanningPlacements(placements, session, cancellationToken) is
                ResourceDeploymentPlanningFenceRejected)
            return new DeclarationPreconditionsChanged();
        if (dependencies.Secrets is { } secrets)
        {
            var fenced = await fencePlanningSecrets(secrets, session, cancellationToken);
            if (fenced is ConsumerSecretReferenceUnavailable unavailable)
                return new DeclarationReadBlocked(new WebServiceSecretReferenceRejected(unavailable));
            if (fenced is ConsumerSecretPlanningChanged)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }
}
