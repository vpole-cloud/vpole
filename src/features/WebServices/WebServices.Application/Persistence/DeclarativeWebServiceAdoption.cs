using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Application.Common;

namespace Pole.Features.WebServices.Application.Persistence;

internal sealed partial class DeclarativeWebServiceProvider
{
    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await services.Find(session, WebServiceFilters.ById(request.TenantId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null || current.Lifecycle is DeletingWebServiceLifecycle)
            return PlanningBlocked(WebServiceDeclarationError.ResourceDeleting);
        if (current.Name != request.Declaration.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        if (await loadPlanningPlacements(new(request.TenantId, ResourceType, current.Id), session, cancellationToken)
            is not ResourceDeploymentPlanningSnapshot { Items.Count: 1 } placements ||
            placements.Items[0].State == ResourceDeploymentPlanningState.Deleting)
            return PlanningBlocked(WebServiceDeclarationError.ImmutablePlacementChange);

        var technical = await ReadPreviewReferencesAsync(new(request.TenantId, request.Declaration,
            request.References), session, cancellationToken);
        if (technical is DeclarationReadBlocked blocked)
            return blocked;
        var additional = ((GitPreviewReferencesSnapshot)technical.Value).References;
        var references = request.References.Concat(additional).GroupBy(item => item.Address)
            .Select(group => group.Last()).ToArray();
        var spec = DeclarationJson.Read<WebServiceDeclaration>(request.Declaration.Spec);
        var groupId = PreviewReference(references, new("ResourceGroup", spec.ResourceGroup))!.ResourceId;
        var scopeId = PreviewReference(references, spec.Placement.IsolationScope is { } scopeName
            ? new("IsolationScope", scopeName) : new("DefaultIsolationScope", spec.ResourceGroup))!.ResourceId;
        var targetId = PreviewReference(references,
            new("DeploymentTarget", spec.Placement.DeploymentTarget))!.ResourceId;
        var computeId = PreviewReference(references,
            new("ComputeProfile", spec.Placement.ComputeProfile.Name))!.ResourceId;
        if (await resolvePlanningTarget(
                new(request.TenantId, ResourceType, scopeId, targetId, RequiredCapabilities(spec)),
                session, cancellationToken) is not DeploymentTargetPlanningSnapshot target ||
            await resolvePlanningCompute(new(request.TenantId, ResourceType, computeId, targetId,
                spec.Placement.ComputeProfile.Revision), session, cancellationToken)
            is not ComputeProfilePlanningSnapshot compute)
            return PlanningBlocked(WebServiceDeclarationError.PinnedComputeProfileUnavailable);

        var secretReferences = ImmutableDictionary.CreateBuilder<string, SecretEnvironmentReference>();
        foreach (var (key, secret) in spec.SecretEnvironment)
        {
            var address = new ResourceAddress("Secret", secret.Secret);
            if (PreviewReference(references, address) is not { } found)
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));
            secretReferences.Add(key, new(found.ResourceId, secret.Key));
        }
        ImmutableDictionary<string, ConnectionEnvironmentReference>? connectionReferences = null;
        if (spec.ConnectionEnvironment is { } connectionEnvironment)
        {
            var values = ImmutableDictionary.CreateBuilder<string, ConnectionEnvironmentReference>();
            foreach (var (key, connection) in connectionEnvironment)
            {
                var address = new ResourceAddress("ResourceConnection", connection.Connection);
                if (PreviewReference(references, address) is not { } found)
                    return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));
                values.Add(key, new(found.ResourceId, connection.ValueKind, connection.ConfigurationKey));
            }
            connectionReferences = values.ToImmutable();
        }
        string? buildId = null;
        if (spec.Image is DeclaredSourceBuildImage build)
        {
            var address = new ResourceAddress("SourceBuildConfiguration", build.SourceBuildConfiguration);
            if (PreviewReference(references, address) is not { } found)
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(address));
            buildId = found.ResourceId;
        }

        var placement = placements.Items[0];
        var identity = new PlanningPlacement(current.ResourceGroupId, current.IsolationScopeId,
            placement.DeploymentTargetId, current.Spec.Image.SourceBuildConfigurationId);
        var projected = Project(current.Spec, current.Tags, current.Exposure.Mode.ToString(), identity,
            new(placement.Compute.ComputeProfileId, placement.Compute.ComputeProfileRevision, placement.FixedReplicas,
                ComputeProjection(placement.Compute)), IngressProjection(placement.Ingress));
        var desired = Project(ToSpec(spec, secretReferences.ToImmutable(), current.Spec, connectionReferences, buildId),
            spec.Tags, spec.Exposure, new(groupId, scopeId, targetId, buildId),
            new(computeId, spec.Placement.ComputeProfile.Revision, spec.FixedReplicas,
                ComputeProjection(compute.Compute.ToResourceDeploymentConfiguration())),
            DesiredIngress(spec, target.Target));
        if (!System.Text.Json.JsonElement.DeepEquals(projected.Identity, desired.Identity))
            return new DeclarationReadBlocked(new DeclarationReplacementRequired());

        if (references.All(item => item.State == GitPreviewReferenceState.Existing))
        {
            var checkedPlan = await ReadPlanningWithReferencesAsync(new(request.TenantId, request.ResourceId,
                request.Declaration, DeclarationPhase.Configure,
                address => Task.FromResult(PreviewReference(references, address)?.ResourceId)),
                session, cancellationToken);
            if (checkedPlan is DeclarationReadBlocked rejected)
                return rejected;
            var dependencies = (WebServicePlanningDependencies)checkedPlan.Value;
            desired = desired with { Dependencies = new(DependencyVersions(dependencies)) };
        }

        return new GitLifecycleAdoptionPreview(new(current.Id, new(LifecycleVersions(current, placements.Items)),
            projected, current.Lifecycle is PreparedWebServiceLifecycle ? DeclarationConfigurationState.Prepared :
                DeclarationConfigurationState.Configured), desired, additional);
    }

    public async Task<AdoptLifecycleResourceOutcome> AdoptAsync(
        AdoptLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var read = await ReadLifecycleAsync(new(request.Authority.TenantId,
            new(operation.Declaration.Address, operation.ResourceId)), session, cancellationToken);

        // The approved vector includes this resource and its accepted dependencies.
        if (read is not GitLifecycleResourceSnapshot snapshot ||
            !snapshot.Version.Documents.All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();
        if (!await git.AuthorizeAsync(new(request.Authority.TenantId, Kind, operation.ResourceId,
                Lifecycle: request.Authority, LifecycleEffect: GitLifecycleMutationEffect.Adopt),
                session, cancellationToken))
            return new StaleGitExecution();

        var current = await services.Find(session, WebServiceFilters.ById(request.Authority.TenantId,
            operation.ResourceId)).SingleAsync(cancellationToken);
        var spec = DeclarationJson.Read<WebServiceDeclaration>(operation.Declaration.Spec);
        var targetId = await AcceptedReferenceAsync(operation, new("DeploymentTarget", spec.Placement.DeploymentTarget),
            session, cancellationToken);
        var computeId = await AcceptedReferenceAsync(operation,
            new("ComputeProfile", spec.Placement.ComputeProfile.Name),
            session, cancellationToken);
        var groupId = await AcceptedReferenceAsync(operation, new("ResourceGroup", spec.ResourceGroup),
            session, cancellationToken);
        string? scopeId = null;
        if (groupId is not null)
        {
            if (spec.Placement.IsolationScope is { } scopeName)
                scopeId = await AcceptedReferenceAsync(operation, new("IsolationScope", scopeName),
                    session, cancellationToken);
            else if (await loadPlanningScope(new(current.TenantId, groupId, null), session, cancellationToken)
                is IsolationScopePlanningSnapshot defaultScope)
                scopeId = defaultScope.IsolationScopeId;
        }
        var buildId = spec.Image is DeclaredSourceBuildImage build
            ? await AcceptedReferenceAsync(operation, new("SourceBuildConfiguration", build.SourceBuildConfiguration),
                session, cancellationToken) : null;
        if (groupId != current.ResourceGroupId || scopeId != current.IsolationScopeId ||
            current.Spec.Image.SourceBuildConfigurationId != buildId)
            return new DeclarationReadBlocked(new DeclarationReplacementRequired());

        if (targetId is null || computeId is null ||
            await loadPlanningPlacements(new(current.TenantId, ResourceType, current.Id), session, cancellationToken)
                is not ResourceDeploymentPlanningSnapshot { Items.Count: 1 } placements ||
            placements.Items[0].DeploymentTargetId != targetId)
            return PlanningBlocked(WebServiceDeclarationError.ImmutablePlacementChange);

        var fenced = await FenceAsync(new(operation, request.Expected), session, cancellationToken);
        if (fenced is DeclarationReadBlocked blocked)
            return blocked;
        if (fenced is DeclarationPreconditionsChanged changed)
            return changed;

        // The shared fence advanced the root token; bind metadata against that written version.
        current = await services.Find(session, WebServiceFilters.ById(request.Authority.TenantId,
            operation.ResourceId)).SingleAsync(cancellationToken);
        var bound = current.BindDeclaredPlacement(new(spec.Placement.DeploymentTarget, targetId,
            spec.Placement.ComputeProfile.Name, computeId));
        var result = await services.UpdateOneAsync(session, WebServiceFilters.AtPlanningVersion(current),
            Builders<WebService>.Update.Set(item => item.DeclaredPlacement, bound.DeclaredPlacement)
                .Inc(item => item.PlanningFence, 1), cancellationToken: cancellationToken);
        return result.ModifiedCount == 1 ? new GitLifecycleResourceAdopted() : new DeclarationPreconditionsChanged();
    }
}
