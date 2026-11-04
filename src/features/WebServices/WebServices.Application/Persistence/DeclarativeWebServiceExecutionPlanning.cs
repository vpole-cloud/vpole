using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

internal sealed partial class DeclarativeWebServiceProvider
{
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
            placement.DeploymentTargetId);
        var projection = operation.Phase == DeclarationPhase.Prepare
            ? new DeclarationProjection(DeclarationJson.Write(identity), DeclarationJson.Write(new { }))
            : Project(current.Spec, current.Tags, current.Exposure.Mode.ToString(), identity,
                new(placement.Compute.ComputeProfileId, placement.Compute.ComputeProfileRevision,
                    placement.FixedReplicas, ComputeProjection(placement.Compute)),
                IngressProjection(placement.Ingress));
        return new CurrentDeclaration(current.Id, new([
                new($"WebService/{current.Id}", current.Revision),
                new($"WebService/{current.Id}/PlanningFence", current.PlanningFence),
                .. placements.Items.Select(item =>
                    new DeclarationDocumentVersion($"ResourceDeployment/{item.DeploymentId}", item.Revision))]),
            projection, operation.Phase == DeclarationPhase.Prepare ||
                current.Lifecycle is not PreparedWebServiceLifecycle
                ? DeclarationConfigurationState.Configured : DeclarationConfigurationState.Prepared);
    }

    private async Task<ProjectDesiredDeclarationOutcome> ProjectAcceptedDesiredAsync(GitOperationInput operation,
        IClientSessionHandle session, CancellationToken ct)
    {
        var read = await ReadExecutionPlanningAsync(operation, session, ct);
        if (read is DeclarationReadBlocked blocked)
            return blocked;

        var dependencies = (WebServicePlanningDependencies)read.Value;
        var spec = DeclarationJson.Read<WebServiceDeclaration>(operation.Declaration.Spec);
        var identity = new PlanningPlacement(dependencies.Scope.ResourceGroupId,
            dependencies.Scope.IsolationScopeId, dependencies.Target.Target.Id);
        var projection = operation.Phase == DeclarationPhase.Prepare
            ? new DeclarationProjection(DeclarationJson.Write(identity), DeclarationJson.Write(new { }))
            : Project(ToSpec(spec, dependencies.References, dependencies.Current?.Spec), spec.Tags, spec.Exposure,
                identity, new(dependencies.Compute.Compute.ComputeProfileId, spec.Placement.ComputeProfile.Revision,
                    spec.FixedReplicas,
                    ComputeProjection(dependencies.Compute.Compute.ToResourceDeploymentConfiguration())),
                DesiredIngress(spec, dependencies.Target.Target));
        return projection with { Dependencies = new(DependencyVersions(dependencies)) };
    }

    private async Task<ReadWebServicePlanningOutcome> ReadExecutionPlanningAsync(GitOperationInput operation,
        IClientSessionHandle session, CancellationToken ct)
    {
        var spec = DeclarationJson.Read<WebServiceDeclaration>(operation.Declaration.Spec);
        var tenant = operation.Context.TenantId;
        var groupId = await AcceptedReferenceAsync(operation, new("ResourceGroup", spec.ResourceGroup), session, ct);
        if (groupId is null)
            return PlanningBlocked(WebServiceDeclarationError.ResourceGroupUnavailable);

        string? scopeId = null;
        if (spec.Placement.IsolationScope is not null)
        {
            scopeId = await AcceptedReferenceAsync(operation,
                new("IsolationScope", spec.Placement.IsolationScope), session, ct);
            if (scopeId is null)
                return PlanningBlocked(WebServiceDeclarationError.ResourceGroupUnavailable);
        }
        if (await loadPlanningScope(new(tenant, groupId, scopeId), session, ct)
            is not IsolationScopePlanningSnapshot scope)
            return PlanningBlocked(WebServiceDeclarationError.ResourceGroupUnavailable);

        var targetId = await AcceptedReferenceAsync(operation,
            new("DeploymentTarget", spec.Placement.DeploymentTarget), session, ct);
        if (targetId is null || await resolvePlanningTarget(new(tenant, WebServiceResourceDeployment.ResourceType,
                scope.IsolationScopeId, targetId, RequiredCapabilities(spec)), session, ct)
            is not DeploymentTargetPlanningSnapshot target)
            return PlanningBlocked(WebServiceDeclarationError.DeploymentTargetUnavailable);

        var profileId = await AcceptedReferenceAsync(operation,
            new("ComputeProfile", spec.Placement.ComputeProfile.Name), session, ct);
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
            var id = await AcceptedReferenceAsync(operation, new("Secret", reference.Secret), session, ct);
            if (id is null)
                return PlanningBlocked(WebServiceDeclarationError.SecretUnavailable);
            references.Add(name, new(id, reference.Key));
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
            references.ToImmutable());
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(FenceDeclarationRequest request,
        IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var loaded = await ReadExecutionPlanningAsync(request.Operation, session, cancellationToken);
        if (loaded is DeclarationReadBlocked blocked)
            return blocked;
        var dependencies = (WebServicePlanningDependencies)loaded.Value;
        if (!DependencyVersions(dependencies).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();
        if (dependencies.Current is { } current)
        {
            if (!request.Expected.Documents.Contains(new($"WebService/{current.Id}", current.Revision)) ||
                !request.Expected.Documents.Contains(
                    new($"WebService/{current.Id}/PlanningFence", current.PlanningFence)))
                return new DeclarationPreconditionsChanged();
            var write = await services.UpdateOneAsync(session, WebServiceFilters.AtPlanningVersion(current),
                Builders<WebService>.Update.Inc(item => item.PlanningFence, 1), cancellationToken: cancellationToken);
            if (write.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        if (await fencePlanningScope(dependencies.Scope, session, cancellationToken)
            is IsolationScopePlanningFenceRejected ||
            await fencePlanningTarget(dependencies.Target, session, cancellationToken)
            is DeploymentTargetPlanningFenceRejected ||
            await fencePlanningCompute(dependencies.Compute, session, cancellationToken)
            is ComputeProfilePlanningFenceRejected)
            return new DeclarationPreconditionsChanged();
        if (dependencies.Placements is { } placements &&
            await fencePlanningPlacements(placements, session, cancellationToken)
            is ResourceDeploymentPlanningFenceRejected)
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

    private static DeploymentTargetRequiredCapabilities RequiredCapabilities(WebServiceDeclaration spec) =>
        spec.Exposure == WebServiceDeclarationRules.PublicExposure
            ? DeploymentTargetRequiredCapabilities.PublicIngress
            : DeploymentTargetRequiredCapabilities.None;

    private Task<string?> AcceptedReferenceAsync(GitOperationInput operation, ResourceAddress address,
        IClientSessionHandle session, CancellationToken ct) => git.ResolveAcceptedAsync(
        new(operation.Context, operation.ResourceId, address), session, ct);

    private static DeclarationDocumentVersion[] DependencyVersions(WebServicePlanningDependencies dependencies) =>
    [
        new($"ResourceGroup/{dependencies.Scope.ResourceGroupId}", dependencies.Scope.ResourceGroupRevision),
        new($"IsolationScope/{dependencies.Scope.IsolationScopeId}", dependencies.Scope.ScopeRevision),
        new($"DeploymentTarget/{dependencies.Target.Target.Id}", dependencies.Target.Target.Revision),
        new($"DeploymentTarget/{dependencies.Target.Target.Id}/WorkloadConfiguration",
            dependencies.Target.Target.WorkloadConfiguration.Revision),
        new($"ComputeProfile/{dependencies.Compute.Request.ComputeProfileId}/Policy",
            dependencies.Compute.PolicyRevision),
        .. (dependencies.Placements?.Items ?? []).Select(item =>
            new DeclarationDocumentVersion($"ResourceDeployment/{item.DeploymentId}", item.Revision)),
        .. (dependencies.Secrets?.Secrets ?? []).SelectMany(item => new DeclarationDocumentVersion[]
        {
            new($"Secret/{item.SecretId}", item.Revision),
            new($"Secret/{item.SecretId}/ReferenceFence", item.ReferenceFence)
        })
    ];

    private static JsonElement ComputeProjection(ResourceDeploymentComputeConfiguration compute) =>
        DeclarationJson.Write(new
        {
            compute.Requests,
            compute.Limits,
            compute.Architecture,
            compute.RuntimeClassName,
            extendedResources = compute.ExtendedResources.OrderBy(item => item.Name, StringComparer.Ordinal),
            nodeSelector = compute.NodeSelector.OrderBy(item => item.Key, StringComparer.Ordinal),
            requiredNodeAffinity = compute.RequiredNodeAffinity.Select(NormalizeTerm)
                .OrderBy(item => DeclarationJson.Write(item).GetRawText(), StringComparer.Ordinal),
            preferredNodeAffinity = compute.PreferredNodeAffinity.Select(item =>
                    item with { Term = NormalizeTerm(item.Term) })
                .OrderBy(item => DeclarationJson.Write(item).GetRawText(), StringComparer.Ordinal),
            tolerations = compute.Tolerations.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ThenBy(item => item.Operator).ThenBy(item => item.Value, StringComparer.Ordinal)
                .ThenBy(item => item.Effect).ThenBy(item => item.TolerationSeconds)
        });

    private static ResourceDeploymentNodeAffinityTerm NormalizeTerm(ResourceDeploymentNodeAffinityTerm term) =>
        term with
        {
            Expressions = term.Expressions.Select(item => item with
            { Values = item.Values.Order(StringComparer.Ordinal).ToImmutableList() })
                .OrderBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.Operator).ToImmutableList()
        };
}

internal sealed record WebServicePlanningDependencies(
    WebService? Current,
    IsolationScopePlanningSnapshot Scope,
    DeploymentTargetPlanningSnapshot Target,
    ComputeProfilePlanningSnapshot Compute,
    ResourceDeploymentPlanningSnapshot? Placements,
    ConsumerSecretPlanningSnapshot? Secrets,
    ImmutableDictionary<string, SecretEnvironmentReference> References);

internal union ReadWebServicePlanningOutcome(
    WebServicePlanningDependencies,
    DeclarationReadBlocked);
