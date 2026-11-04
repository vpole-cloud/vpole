using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Contracts;
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

        var placements = await loadDeployments(new(request.TenantId,
            WebServiceResourceDeployment.ResourceType, current.Id), cancellationToken);
        if (placements.Count != 1 || placements[0].DeploymentTargetId != current.DeclaredPlacement.DeploymentTargetId ||
            placements[0].IsolationScopeId != current.IsolationScopeId)
            return PlanningBlocked(WebServiceDeclarationError.ImmutablePlacementChange);

        var placement = placements[0];
        var projection = Project(current.Spec, current.Tags, current.Exposure.Mode.ToString(),
            new(current.ResourceGroupId, current.IsolationScopeId, placement.DeploymentTargetId),
            new(placement.Compute.Profile.ComputeProfileId, placement.Compute.Profile.ComputeProfileRevision,
                placement.FixedReplicas, null));
        return new CurrentDeclaration(current.Id, new([
                new($"WebService/{current.Id}", current.Revision),
                new($"ResourceDeployment/{placement.DeploymentId}", placement.DeploymentRevision)]),
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
        if (groupId is null || await groupOwnership(request.TenantId, groupId, cancellationToken)
            is not ResourceGroupOwnership group)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(groupAddress));

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

        string profileId;
        if (bound?.ComputeProfileName == spec.Placement.ComputeProfile.Name)
        {
            profileId = bound.ComputeProfileId;
            var placements = await loadDeployments(new(request.TenantId,
                WebServiceResourceDeployment.ResourceType, current!.Id), cancellationToken);
            var alreadyPinned = placements.Count == 1 && placements[0].DeploymentTargetId == targetId &&
                placements[0].Compute.Profile.ComputeProfileId == profileId &&
                placements[0].Compute.Profile.ComputeProfileRevision == spec.Placement.ComputeProfile.Revision;
            if (!alreadyPinned)
            {
                var resolution = await resolveCompute(new(request.TenantId, WebServiceResourceDeployment.ResourceType,
                    [new(targetId, profileId, spec.Placement.ComputeProfile.Revision)]), cancellationToken);
                if (resolution is ComputeProfileResolutionError error)
                    return PlanningBlocked(error.Value is ComputeProfileUnavailable
                    {
                        Reason: ComputeProfileUnavailableReason.RevisionUnavailable
                    }
                        ? WebServiceDeclarationError.ComputeProfileRevisionUnavailable
                        : WebServiceDeclarationError.PinnedComputeProfileUnavailable);
            }
        }
        else if (await resolveProfileName(new(request.TenantId, WebServiceResourceDeployment.ResourceType,
            spec.Placement.ComputeProfile.Name, targetId, spec.Placement.ComputeProfile.Revision), cancellationToken)
            is ResolvedComputeProfile profile)
            profileId = profile.ComputeProfileId;
        else
            return PlanningBlocked(WebServiceDeclarationError.PinnedComputeProfileUnavailable);

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
            new(groupId, group.DefaultIsolationScopeId, targetId),
            new(profileId, spec.Placement.ComputeProfile.Revision, spec.Replicas, null));
    }

    private sealed record PlanningPlacement(
        string ResourceGroupId,
        string IsolationScopeId,
        string DeploymentTargetId);

    private sealed record PlanningCompute(
        string ProfileId,
        long Revision,
        int Replicas,
        JsonElement? Resolution);

    private static DeclarationProjection Project(
        WebServiceSpec spec,
        IEnumerable<string> tags,
        string exposure,
        PlanningPlacement placement,
        PlanningCompute compute) => new(DeclarationJson.Write(placement), DeclarationJson.Write(new
        {
            image = new { spec.Image.Server, spec.Image.Name, spec.Image.Tag },
            ports = spec.Config.PortMappings.OrderBy(port => port.Name, StringComparer.Ordinal).ToArray(),
            environment = spec.Config.EnvironmentVariables,
            secretEnvironment = spec.Config.SecretEnvironmentReferences,
            probes = spec.Probes,
            autoscaling = spec.Autoscaling,
            tags = tags.Select(tag => tag.Trim()).Distinct().Order(StringComparer.Ordinal).ToArray(),
            exposure,
            compute
        }));

    private static DeclarationReadBlocked PlanningBlocked(WebServiceDeclarationError reason) =>
        new(new WebServiceDeclarationRejected(reason));
}
