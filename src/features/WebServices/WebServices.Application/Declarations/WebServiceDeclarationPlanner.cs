using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application;
using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using static Pole.Features.WebServices.Application.Declarations.WebServiceDeclarations;
using static Pole.Features.WebServices.Application.Declarations.WebServicePlanning;

namespace Pole.Features.WebServices.Application.Declarations;

internal sealed class WebServiceDeclarationPlanner(
    IMongoCollection<WebService> services,
    IGitOpsMutations git,
    WebServicePlanningReader planningReader,
    LoadIsolationScopePlanningSnapshot loadPlanningScope,
    LoadResourceDeploymentPlanningSnapshot loadPlanningPlacements,
    ResolveComputeProfilePlanningSnapshot resolvePlanningCompute,
    ResolveDeploymentTargetPlanningSnapshot resolvePlanningTarget,
    ResolveNamedDeploymentTarget resolveTargetName,
    ResolveNamedComputeProfile resolveProfileName)
{
    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (request.Operation is not null)
            return await ReadAcceptedCurrentAsync(request, session, cancellationToken);

        var current = await services.Find(session,
            WebServiceFilters.ByTechnicalName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.Lifecycle is DeletingWebServiceLifecycle)
            return PlanningBlocked(WebServiceDeclarationError.ResourceDeleting);

        if (current.DeclaredPlacement is null)
            return PlanningBlocked(WebServiceDeclarationError.PreparedPlacementIdentityUnavailable);

        var loaded = await loadPlanningPlacements(new(request.TenantId,
            WebServiceResourceDeployment.ResourceType, current.Id), session, cancellationToken);
        if (loaded is not ResourceDeploymentPlanningSnapshot placements || placements.Items.Count != 1 ||
            placements.Items[0].DeploymentTargetId != current.DeclaredPlacement.DeploymentTargetId ||
            placements.Items[0].IsolationScopeId != current.IsolationScopeId)
            return PlanningBlocked(WebServiceDeclarationError.ImmutablePlacementChange);

        var placement = placements.Items[0];
        var projection = Project(current.Spec, current.Tags, current.Exposure.Mode.ToString(),
            new(current.ResourceGroupId, current.IsolationScopeId, placement.DeploymentTargetId),
            new(placement.Compute.ComputeProfileId, placement.Compute.ComputeProfileRevision,
                placement.FixedReplicas, ComputeProjection(placement.Compute)), IngressProjection(placement.Ingress));
        return new CurrentDeclaration(current.Id, new([
                new(WebServiceDeclarations.VersionKey(current.Id), current.Revision),
                new(ResourceDeploymentVersionKeys.Revision(placement.DeploymentId), placement.Revision)]),
            projection, current.Lifecycle is PreparedWebServiceLifecycle
                ? DeclarationConfigurationState.Prepared : DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (request.Operation is not null)
            return await ProjectAcceptedDesiredAsync(request.Operation, session, cancellationToken);

        var spec = DeclarationJson.Read<WebServiceDeclaration>(NormalizeAndValidate(request.Declaration.Spec));
        var groupAddress = new ResourceAddress("ResourceGroup", spec.ResourceGroup);
        var groupId = await git.ResolveAsync(new(request.TenantId, groupAddress), session, cancellationToken);
        if (groupId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(groupAddress));

        string? scopeId = null;
        if (spec.Placement.IsolationScope is not null)
        {
            var address = new ResourceAddress("IsolationScope", spec.Placement.IsolationScope);
            scopeId = await git.ResolveAsync(new(request.TenantId, address), session, cancellationToken);
            if (scopeId is null)
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));
        }
        if (await loadPlanningScope(new(request.TenantId, groupId, scopeId), session, cancellationToken)
            is not IsolationScopePlanningSnapshot scope)
            return PlanningBlocked(WebServiceDeclarationError.ResourceGroupUnavailable);

        var current = await services.Find(session,
            WebServiceFilters.ByTechnicalName(request.TenantId, request.Declaration.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        var bound = current?.DeclaredPlacement;
        string targetId;
        if (bound?.DeploymentTargetName == spec.Placement.DeploymentTarget)
            targetId = bound.DeploymentTargetId;
        else if (await resolveTargetName(new(request.TenantId, WebServiceResourceDeployment.ResourceType,
            spec.Placement.DeploymentTarget), cancellationToken) is DeploymentTargetLookupDto target)
            targetId = target.Id;
        else
            return PlanningBlocked(WebServiceDeclarationError.DeploymentTargetUnavailable);

        var targetRead = await resolvePlanningTarget(new(request.TenantId,
            WebServiceResourceDeployment.ResourceType, scope.IsolationScopeId, targetId, RequiredCapabilities(spec)),
            session, cancellationToken);
        if (targetRead is not DeploymentTargetPlanningSnapshot targetSnapshot)
            return PlanningBlocked(WebServiceDeclarationError.DeploymentTargetUnavailable);

        string profileId;
        if (bound?.ComputeProfileName == spec.Placement.ComputeProfile.Name)
            profileId = bound.ComputeProfileId;
        else if (await resolveProfileName(new(request.TenantId, WebServiceResourceDeployment.ResourceType,
            spec.Placement.ComputeProfile.Name, targetId, spec.Placement.ComputeProfile.Revision), cancellationToken)
            is ResolvedComputeProfile profile)
            profileId = profile.ComputeProfileId;
        else
            return PlanningBlocked(WebServiceDeclarationError.PinnedComputeProfileUnavailable);

        var computeRead = await resolvePlanningCompute(new(request.TenantId,
            WebServiceResourceDeployment.ResourceType, profileId, targetId, spec.Placement.ComputeProfile.Revision),
            session, cancellationToken);
        if (computeRead is not ComputeProfilePlanningSnapshot compute)
            return PlanningBlocked(computeRead is ComputeProfilePlanningUnavailable
            {
                Error.Value: ComputeProfileUnavailable
                { Reason: ComputeProfileUnavailableReason.RevisionUnavailable }
            }
                ? WebServiceDeclarationError.ComputeProfileRevisionUnavailable
                : WebServiceDeclarationError.PinnedComputeProfileUnavailable);

        var references = ImmutableDictionary.CreateBuilder<string, SecretEnvironmentReference>();
        foreach (var (name, reference) in spec.SecretEnvironment)
        {
            var address = new ResourceAddress("Secret", reference.Secret);
            var id = await git.ResolveAsync(new(request.TenantId, address), session, cancellationToken);
            if (id is null)
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));

            references.Add(name, new(id, reference.Key));
        }

        ImmutableDictionary<string, ConnectionEnvironmentReference>? connections = null;
        if (spec.ConnectionEnvironment is not null)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, ConnectionEnvironmentReference>();
            foreach (var (name, reference) in spec.ConnectionEnvironment)
            {
                var address = new ResourceAddress("ResourceConnection", reference.Connection);
                var id = await git.ResolveAsync(new(request.TenantId, address), session, cancellationToken);
                if (id is null)
                    return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));

                builder.Add(name, new(id, reference.ValueKind, reference.ConfigurationKey));
            }
            connections = builder.ToImmutable();
        }

        string? buildId = null;
        if (spec.Image is DeclaredSourceBuildImage image)
        {
            var address = new ResourceAddress("SourceBuildConfiguration", image.SourceBuildConfiguration);
            buildId = await git.ResolveAsync(new(request.TenantId, address), session, cancellationToken);
            if (buildId is null)
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));
        }

        return Project(ToSpec(spec, references.ToImmutable(), current?.Spec, connections, buildId),
            spec.Tags, spec.Exposure,
            new(groupId, scope.IsolationScopeId, targetId),
            new(profileId, spec.Placement.ComputeProfile.Revision, spec.FixedReplicas,
                ComputeProjection(compute.Compute.ToResourceDeploymentConfiguration())),
            DesiredIngress(spec, targetSnapshot.Target));
    }

    private async Task<ReadCurrentDeclarationOutcome> ReadAcceptedCurrentAsync(ReadCurrentDeclarationRequest request,
        IClientSessionHandle session, CancellationToken ct)
    {
        var operation = request.Operation!;
        var current = await services.Find(session, WebServiceFilters.ById(request.TenantId, operation.ResourceId))
            .FirstOrDefaultAsync(ct);
        current ??= await services.Find(session,
                WebServiceFilters.ByTechnicalName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new CurrentDeclarationMissing();
        if (current.Id == operation.ResourceId && current.Name != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        if (current.Lifecycle is DeletingWebServiceLifecycle)
            return PlanningBlocked(WebServiceDeclarationError.ResourceDeleting);
        if (current.DeclaredPlacement is null)
            return PlanningBlocked(WebServiceDeclarationError.PreparedPlacementIdentityUnavailable);

        var loaded = await loadPlanningPlacements(new(request.TenantId,
            WebServiceResourceDeployment.ResourceType, current.Id), session, ct);
        if (loaded is not ResourceDeploymentPlanningSnapshot placements || placements.Items.Count != 1)
            return PlanningBlocked(WebServiceDeclarationError.ImmutablePlacementChange);

        var placement = placements.Items[0];
        if (placement.DeploymentTargetId != current.DeclaredPlacement.DeploymentTargetId ||
            placement.ResourceGroupId != current.ResourceGroupId ||
            placement.IsolationScopeId != current.IsolationScopeId ||
            placement.State == ResourceDeploymentPlanningState.Deleting)
            return PlanningBlocked(WebServiceDeclarationError.ImmutablePlacementChange);

        var identity = new PlanningPlacement(current.ResourceGroupId, current.IsolationScopeId,
            placement.DeploymentTargetId, current.Spec.Image.SourceBuildConfigurationId);
        var projection = operation.Phase == DeclarationPhase.Prepare
            ? new DeclarationProjection(DeclarationJson.Write(identity), DeclarationJson.Write(new { }))
            : Project(current.Spec, current.Tags, current.Exposure.Mode.ToString(), identity,
                new(placement.Compute.ComputeProfileId, placement.Compute.ComputeProfileRevision,
                    placement.FixedReplicas, ComputeProjection(placement.Compute)),
                IngressProjection(placement.Ingress));
        return new CurrentDeclaration(current.Id, new([
                new(WebServiceDeclarations.VersionKey(current.Id), current.Revision),
                new(
                    DeclarationVersionKeys.PlanningFence(WebServiceDeclarations.Kind, current.Id),
                    current.PlanningFence),
                .. placements.Items.Select(item =>
                    new DeclarationDocumentVersion(
                        ResourceDeploymentVersionKeys.Revision(item.DeploymentId), item.Revision))]),
            projection, operation.Phase == DeclarationPhase.Prepare ||
                current.Lifecycle is not PreparedWebServiceLifecycle
                ? DeclarationConfigurationState.Configured : DeclarationConfigurationState.Prepared);
    }

    private async Task<ProjectDesiredDeclarationOutcome> ProjectAcceptedDesiredAsync(GitOperationInput operation,
        IClientSessionHandle session, CancellationToken ct)
    {
        var read = await planningReader.ReadExecutionPlanningAsync(operation, session, ct);
        if (read is DeclarationReadBlocked blocked)
            return blocked;

        var dependencies = (WebServicePlanningDependencies)read.Value;
        var spec = DeclarationJson.Read<WebServiceDeclaration>(operation.Declaration.Spec);
        var identity = new PlanningPlacement(dependencies.Scope.ResourceGroupId,
            dependencies.Scope.IsolationScopeId, dependencies.Target.Target.Id, dependencies.BuildConfigurationId);
        var projection = operation.Phase == DeclarationPhase.Prepare
            ? new DeclarationProjection(DeclarationJson.Write(identity), DeclarationJson.Write(new { }))
            : Project(ToSpec(spec, dependencies.References, dependencies.Current?.Spec,
                    dependencies.ConnectionReferences, dependencies.BuildConfigurationId),
                spec.Tags, spec.Exposure,
                identity, new(dependencies.Compute.Compute.ComputeProfileId, spec.Placement.ComputeProfile.Revision,
                    spec.FixedReplicas,
                    ComputeProjection(dependencies.Compute.Compute.ToResourceDeploymentConfiguration())),
                DesiredIngress(spec, dependencies.Target.Target));
        return projection with { Dependencies = new(DependencyVersions(dependencies)) };
    }
}
