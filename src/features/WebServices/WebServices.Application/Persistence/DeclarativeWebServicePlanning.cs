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

namespace Pole.Features.WebServices.Application.Persistence;

internal sealed partial class DeclarativeWebServiceProvider
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
                new($"WebService/{current.Id}", current.Revision),
                new($"ResourceDeployment/{placement.DeploymentId}", placement.Revision)]),
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

        return Project(ToSpec(spec, references.ToImmutable(), current?.Spec), spec.Tags, spec.Exposure,
            new(groupId, scope.IsolationScopeId, targetId),
            new(profileId, spec.Placement.ComputeProfile.Revision, spec.FixedReplicas,
                ComputeProjection(compute.Compute.ToResourceDeploymentConfiguration())),
            DesiredIngress(spec, targetSnapshot.Target));
    }

    private sealed record PlanningPlacement(
        string ResourceGroupId,
        string IsolationScopeId,
        string DeploymentTargetId);

    private sealed record PlanningCompute(
        string ProfileId,
        long Revision,
        int Replicas,
        JsonElement Resolution);

    private static DeclarationProjection Project(
        WebServiceSpec spec,
        IEnumerable<string> tags,
        string exposure,
        PlanningPlacement placement,
        PlanningCompute compute,
        PlanningIngress? ingress) => new(DeclarationJson.Write(placement), DeclarationJson.Write(new
        {
            image = new { spec.Image.Server, spec.Image.Name, spec.Image.Tag },
            ports = spec.Config.PortMappings.OrderBy(port => port.Name, StringComparer.Ordinal).ToArray(),
            environment = spec.Config.EnvironmentVariables,
            secretEnvironment = spec.Config.SecretEnvironmentReferences,
            probes = spec.Probes,
            autoscaling = spec.Autoscaling,
            tags = tags.Select(tag => tag.Trim()).Distinct().Order(StringComparer.Ordinal).ToArray(),
            exposure,
            ingress,
            compute
        }));

    private sealed record PlanningIngress(
        string ClassName,
        string ClusterIssuer,
        string? PublicBaseDomain);

    private static PlanningIngress? IngressProjection(ResourceDeploymentIngressConfiguration? ingress) =>
        ingress is null ? null : new(ingress.IngressClassName, ingress.ClusterIssuerName, ingress.PublicBaseDomain);

    private static PlanningIngress? DesiredIngress(WebServiceDeclaration spec, DeploymentTargetLookupDto target) =>
        spec.Exposure == WebServiceDeclarationRules.PublicExposure &&
        target.WorkloadConfiguration.Ingress is { } ingress
            ? new(ingress.IngressClassName, ingress.ClusterIssuerName, ingress.PublicBaseDomain) : null;

    private static DeclarationReadBlocked PlanningBlocked(WebServiceDeclarationError reason) =>
        new(new WebServiceDeclarationRejected(reason));
}
