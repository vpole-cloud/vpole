using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using static Pole.Features.WebServices.Application.Persistence.WebServiceDeclarations;
using static Pole.Features.WebServices.Application.Persistence.WebServicePlanning;

namespace Pole.Features.WebServices.Application.Persistence;

internal sealed class WebServicePlanningReader(
    IMongoCollection<WebService> services,
    IGitOpsMutations git,
    LoadIsolationScopePlanningSnapshot loadPlanningScope,
    LoadResourceDeploymentPlanningSnapshot loadPlanningPlacements,
    ResolveComputeProfilePlanningSnapshot resolvePlanningCompute,
    ResolveDeploymentTargetPlanningSnapshot resolvePlanningTarget,
    LoadSourceBuildConfigurationReference loadBuildConfiguration,
    LoadResourceConnectionReferencePlanning loadPlanningConnections,
    LoadConsumerSecretPlanningSnapshot loadPlanningSecrets)
{
    internal Task<ReadWebServicePlanningOutcome> ReadExecutionPlanningAsync(GitOperationInput operation,
        IClientSessionHandle session, CancellationToken ct) =>
        ReadPlanningWithReferencesAsync(new(operation.Context.TenantId, operation.ResourceId, operation.Declaration,
            operation.Phase, address => AcceptedReferenceAsync(operation, address, session, ct)), session, ct);

    internal async Task<ReadWebServicePlanningOutcome> ReadPlanningWithReferencesAsync(
        WebServicePlanningInput operation, IClientSessionHandle session, CancellationToken ct)
    {
        var spec = DeclarationJson.Read<WebServiceDeclaration>(operation.Declaration.Spec);
        var tenant = operation.TenantId;
        var groupId = await operation.Reference(new("ResourceGroup", spec.ResourceGroup));
        if (groupId is null)
            return PlanningBlocked(WebServiceDeclarationError.ResourceGroupUnavailable);

        string? scopeId = null;
        if (spec.Placement.IsolationScope is not null)
        {
            scopeId = await operation.Reference(new("IsolationScope", spec.Placement.IsolationScope));
            if (scopeId is null)
                return PlanningBlocked(WebServiceDeclarationError.ResourceGroupUnavailable);
        }
        if (await loadPlanningScope(new(tenant, groupId, scopeId), session, ct)
            is not IsolationScopePlanningSnapshot scope)
            return PlanningBlocked(WebServiceDeclarationError.ResourceGroupUnavailable);

        var targetId = await operation.Reference(new("DeploymentTarget", spec.Placement.DeploymentTarget));
        if (targetId is null || await resolvePlanningTarget(new(tenant, WebServiceResourceDeployment.ResourceType,
                scope.IsolationScopeId, targetId, RequiredCapabilities(spec)), session, ct)
            is not DeploymentTargetPlanningSnapshot target)
            return PlanningBlocked(WebServiceDeclarationError.DeploymentTargetUnavailable);

        var profileId = await operation.Reference(new("ComputeProfile", spec.Placement.ComputeProfile.Name));
        if (profileId is null)
            return PlanningBlocked(WebServiceDeclarationError.PinnedComputeProfileUnavailable);
        var resolution = await resolvePlanningCompute(new(tenant, WebServiceResourceDeployment.ResourceType,
            profileId, targetId, spec.Placement.ComputeProfile.Revision), session, ct);
        if (resolution is not ComputeProfilePlanningSnapshot compute)
            return PlanningBlocked(resolution is ComputeProfilePlanningUnavailable
            { Error.Value: ComputeProfileUnavailable { Reason: ComputeProfileUnavailableReason.RevisionUnavailable } }
                ? WebServiceDeclarationError.ComputeProfileRevisionUnavailable
                : WebServiceDeclarationError.PinnedComputeProfileUnavailable);

        var current = await services.Find(session, WebServiceFilters.ById(tenant, operation.ResourceId))
            .FirstOrDefaultAsync(ct);
        ResourceDeploymentPlanningSnapshot? placements = null;
        if (current is not null)
        {
            var loaded = await loadPlanningPlacements(new(tenant, WebServiceResourceDeployment.ResourceType,
                current.Id), session, ct);
            if (loaded is not ResourceDeploymentPlanningSnapshot placed || placed.Items.Count != 1 ||
                placed.Items.Any(item => item.State == ResourceDeploymentPlanningState.Deleting))
                return PlanningBlocked(WebServiceDeclarationError.ImmutablePlacementChange);
            placements = placed;
        }

        var references = ImmutableDictionary.CreateBuilder<string, SecretEnvironmentReference>();
        foreach (var (name, reference) in spec.SecretEnvironment)
        {
            var id = await operation.Reference(new("Secret", reference.Secret));
            if (id is null)
                return PlanningBlocked(WebServiceDeclarationError.SecretUnavailable);
            references.Add(name, new(id, reference.Key));
        }

        ImmutableDictionary<string, ConnectionEnvironmentReference>? connectionReferences = null;
        if (spec.ConnectionEnvironment is not null)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, ConnectionEnvironmentReference>();
            foreach (var (name, reference) in spec.ConnectionEnvironment)
            {
                var id = await operation.Reference(new("ResourceConnection", reference.Connection));
                if (id is null)
                    return new DeclarationReadBlocked(new WebServiceConnectionReferenceRejected());

                builder.Add(name, new(id, reference.ValueKind, reference.ConfigurationKey));
            }
            connectionReferences = builder.ToImmutable();
        }

        ResourceConnectionReferencePlanningSnapshot? connections = null;
        if (operation.Phase == DeclarationPhase.Configure && connectionReferences is not null)
        {
            var readConnections = await loadPlanningConnections(new(tenant, WebServiceResourceDeployment.ResourceType,
                operation.ResourceId, connectionReferences.Values.Select(item => new ResourceConnectionValueReference(
                    item.ConnectionId, item.ValueKind, item.ConfigurationKey)).ToArray()), session, ct);
            if (readConnections is ResourceConnectionReferencesUnavailable)
                return new DeclarationReadBlocked(new WebServiceConnectionReferenceRejected());

            connections = (ResourceConnectionReferencePlanningSnapshot)readConnections.Value;
        }

        string? buildConfigurationId = null;
        SourceBuildConfigurationSnapshot? buildConfiguration = null;
        if (spec.Image is DeclaredSourceBuildImage image)
        {
            buildConfigurationId = await operation.Reference(
                new("SourceBuildConfiguration", image.SourceBuildConfiguration));
            if (buildConfigurationId is null)
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(
                    new("SourceBuildConfiguration", image.SourceBuildConfiguration)));
            if (operation.Phase == DeclarationPhase.Configure)
            {
                var loadedBuild = await loadBuildConfiguration(new(tenant, buildConfigurationId), session, ct);
                if (loadedBuild is not SourceBuildConfigurationSnapshot build ||
                    build.WebServiceId != operation.ResourceId)
                    return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(
                        new("SourceBuildConfiguration", image.SourceBuildConfiguration)));
                buildConfiguration = build;
            }
        }

        ConsumerSecretPlanningSnapshot? secrets = null;
        if (operation.Phase == DeclarationPhase.Configure)
        {
            if (current is null || placements is null)
                return PlanningBlocked(WebServiceDeclarationError.PreparedResourceUnavailable);
            var previous = current.Spec.Config.SecretEnvironmentReferences.Values.Select(item =>
                new ConsumerSecretKeyReference(item.SecretId, item.Key)).ToArray();
            var desired = references.Values.Select(item => new ConsumerSecretKeyReference(item.SecretId, item.Key))
                .ToArray();
            var loaded = await loadPlanningSecrets(new(tenant, scope.ResourceGroupId, scope.IsolationScopeId,
                WebServiceResourceDeployment.ResourceType, current.Id, previous, desired,
                placements.Items.Select(item => item.DeploymentId).ToArray()), session, ct);
            if (loaded is ConsumerSecretReferenceUnavailable unavailable)
                return new DeclarationReadBlocked(new WebServiceSecretReferenceRejected(unavailable));
            secrets = (ConsumerSecretPlanningSnapshot)loaded.Value;
        }

        return new WebServicePlanningDependencies(current, scope, target, compute, placements, secrets,
            references.ToImmutable(), connections, connectionReferences, buildConfigurationId, buildConfiguration);
    }

    internal Task<string?> AcceptedReferenceAsync(GitOperationInput operation, ResourceAddress address,
        IClientSessionHandle session, CancellationToken ct) => git.ResolveAcceptedAsync(
        new(operation.Context, operation.ResourceId, address), session, ct);
}
