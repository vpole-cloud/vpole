using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Functions.Application.Persistence;

internal sealed partial class DeclarativeFunctionProvider
{
    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request, IClientSessionHandle session, CancellationToken ct)
    {
        var current = request.Operation is { } operation
            ? await resources.Find(session, FunctionAppFilters.ByTenantAndId(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(ct) : null;
        current ??= await resources.Find(session, FunctionAppFilters.ByName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.NormalizedName != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var loaded = await ReadCurrentStateAsync(current, session, ct);
        if (loaded is DeclarationReadBlocked blocked)
            return blocked;

        var state = (FunctionPlanningCurrent)loaded.Value;
        var phase = request.Operation?.Phase ?? DeclarationPhase.Configure;
        return new CurrentDeclaration(current.Id, new(CurrentVersions(state)),
            Project(phase, new(current.ResourceGroupId, state.Placements.Items[0].IsolationScopeId,
                current.Placement.DeploymentTargetId, current.FunctionRoute,
                state.Placements.Items[0].Compute, state.Latest?.Spec, current.Tags)),
            phase == DeclarationPhase.Prepare || current.DesiredState == FunctionAppDesiredState.Enabled
                ? DeclarationConfigurationState.Configured : DeclarationConfigurationState.Prepared);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request, IClientSessionHandle session, CancellationToken ct)
    {
        if (request.Operation is null)
            return Blocked(FunctionDeclarationError.AcceptedSnapshotRequired);

        var loaded = await ReadPlanningAsync(request.Operation, session, ct);
        if (loaded is DeclarationReadBlocked blocked)
            return blocked;

        var dependencies = (FunctionPlanningDependencies)loaded.Value;
        var declaration = FunctionDeclarationJson.Read(request.Declaration.Spec);
        var spec = FunctionDeclarationJson.Spec(declaration, dependencies.SecretReferences, null);
        return Project(request.Operation.Phase, new(dependencies.Scope.ResourceGroupId,
            dependencies.Scope.IsolationScopeId, dependencies.Target.Target.Id, declaration.FunctionRoute,
            dependencies.Compute.Compute.ToFunctionResourceDeploymentConfiguration(spec.Resources),
            spec, declaration.Tags))
            with
        { Dependencies = new(DependencyVersions(dependencies)) };
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request, IClientSessionHandle session, CancellationToken ct)
    {
        var loaded = await ReadPlanningAsync(request.Operation, session, ct);
        if (loaded is DeclarationReadBlocked blocked)
            return blocked;

        var dependencies = (FunctionPlanningDependencies)loaded.Value;
        if (!DependencyVersions(dependencies).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        if (dependencies.Current is { } current)
        {
            if (!CurrentVersions(current).All(request.Expected.Documents.Contains))
                return new DeclarationPreconditionsChanged();

            var root = current.Root;
            var fenced = await resources.UpdateOneAsync(session,
                FunctionAppFilters.AtPlanningVersion(root),
                Builders<FunctionApp>.Update.Inc(app => app.PlanningFence, 1), cancellationToken: ct);
            if (fenced.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();

            foreach (var revision in ConfigurationRevisions(current))
            {
                var result = await revisions.UpdateOneAsync(session,
                    FunctionRevisionFilters.AtPlanningVersion(revision),
                    Builders<FunctionRevision>.Update.Inc(item => item.PlanningFence, 1), cancellationToken: ct);
                if (result.ModifiedCount != 1)
                    return new DeclarationPreconditionsChanged();
            }

            var route = current.Route;
            var routeFenced = await routes.UpdateOneAsync(session,
                FunctionRouteBindingFilters.AtPlanningVersion(route),
                Builders<FunctionRouteBinding>.Update.Inc(item => item.PlanningFence, 1), cancellationToken: ct);
            if (routeFenced.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }
        else if (request.Expected.Documents.Any(item =>
                     item.DocumentId == $"FunctionApp/{request.Operation.ResourceId}"))
        {
            return new DeclarationPreconditionsChanged();
        }

        if (await fenceScope(dependencies.Scope, session, ct) is not IsolationScopePlanningFenced ||
            await fenceTarget(dependencies.Target, session, ct) is not DeploymentTargetPlanningFenced ||
            await fenceCompute(dependencies.Compute, session, ct) is not ComputeProfilePlanningFenced)
            return new DeclarationPreconditionsChanged();

        if (dependencies.Current is { } found &&
            await fencePlacements(found.Placements, session, ct) is not ResourceDeploymentPlanningFenced)
            return new DeclarationPreconditionsChanged();

        if (dependencies.Secrets is { } secrets)
        {
            var fenced = await fenceSecrets(secrets, session, ct);
            if (fenced is ConsumerSecretReferenceUnavailable unavailable)
                return new DeclarationReadBlocked(new FunctionDeclarationSecretRejected(unavailable.Reason));

            if (fenced is not ConsumerSecretPlanningFenced)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }

    private async Task<ReadFunctionCurrentOutcome> ReadCurrentStateAsync(
        FunctionApp current, IClientSessionHandle session, CancellationToken ct)
    {
        if (current.DesiredState is not (FunctionAppDesiredState.Prepared or FunctionAppDesiredState.Enabled))
            return Blocked(FunctionDeclarationError.ResourceDeleting);

        if (await loadPlacements(
                new(current.TenantId, FunctionResourceDeployment.ResourceType, current.Id), session, ct)
            is not ResourceDeploymentPlanningSnapshot { Items.Count: 1 } placements ||
            placements.Items[0].State == ResourceDeploymentPlanningState.Deleting ||
            placements.Items[0].ResourceGroupId != current.ResourceGroupId ||
            placements.Items[0].DeploymentId != current.Placement.ResourceDeploymentId ||
            placements.Items[0].DeploymentTargetId != current.Placement.DeploymentTargetId)
            return Blocked(FunctionDeclarationError.PlacementUnavailable);

        var route = await routes.Find(session, FunctionRouteBindingFilters.ByRoute(current.FunctionRoute))
            .FirstOrDefaultAsync(ct);
        if (route is null || route.TenantId != current.TenantId || route.FunctionAppId != current.Id ||
            route.State == FunctionRouteBindingState.Disabled)
            return Blocked(FunctionDeclarationError.FunctionRouteUnavailable);

        var found = await revisions.Find(session, FunctionRevisionFilters.ReferencedConfiguration(current,
            current.ActiveRevisionId is { } active ? [active] : [])).ToListAsync(ct);
        var latest = found.SingleOrDefault(item => item.RevisionNumber == current.NextRevisionNumber - 1);
        var activeRevision = found.SingleOrDefault(item => item.Id == current.ActiveRevisionId);
        if (current.NextRevisionNumber > 1 && latest is null ||
            current.ActiveRevisionId is not null && activeRevision is null)
            return Blocked(FunctionDeclarationError.ConfigurationUnavailable);

        return new FunctionPlanningCurrent(current, latest, activeRevision, route, placements);
    }

    private async Task<ReadFunctionPlanningOutcome> ReadPlanningAsync(
        GitOperationInput operation, IClientSessionHandle session, CancellationToken ct)
    {
        var declaration = FunctionDeclarationJson.Read(operation.Declaration.Spec);
        var context = operation.Context;
        var groupId = await git.ResolveAcceptedAsync(new(context, operation.ResourceId,
            new("ResourceGroup", declaration.ResourceGroup)), session, ct);
        if (groupId is null)
            return Blocked(FunctionDeclarationError.ResourceGroupUnavailable);

        var scopeId = declaration.Placement.IsolationScope is { } scopeName
            ? await git.ResolveAcceptedAsync(
                new(context, operation.ResourceId, new("IsolationScope", scopeName)), session, ct)
            : null;
        if (declaration.Placement.IsolationScope is not null && scopeId is null)
            return Blocked(FunctionDeclarationError.IsolationScopeUnavailable);

        if (await loadScope(new(context.TenantId, groupId, scopeId), session, ct)
            is not IsolationScopePlanningSnapshot scope)
            return Blocked(FunctionDeclarationError.IsolationScopeUnavailable);

        var targetId = await git.ResolveAcceptedAsync(new(context, operation.ResourceId,
            new("DeploymentTarget", declaration.Placement.DeploymentTarget)), session, ct);
        if (targetId is null || await loadTarget(new(context.TenantId, FunctionResourceDeployment.ResourceType,
                scope.IsolationScopeId, targetId, DeploymentTargetRequiredCapabilities.None,
                FunctionResourceDeployment.GatewaySourceName), session, ct)
            is not DeploymentTargetPlanningSnapshot target)
            return Blocked(FunctionDeclarationError.DeploymentTargetUnavailable);

        var computeId = await git.ResolveAcceptedAsync(new(context, operation.ResourceId,
            new("ComputeProfile", declaration.Placement.ComputeProfile.Name)), session, ct);
        if (computeId is null || await loadCompute(new(context.TenantId, FunctionResourceDeployment.ResourceType,
                computeId, targetId, declaration.Placement.ComputeProfile.Revision), session, ct)
            is not ComputeProfilePlanningSnapshot compute)
            return Blocked(FunctionDeclarationError.ComputeProfileUnavailable);

        var root = await resources.Find(session,
                FunctionAppFilters.ByTenantAndId(context.TenantId, operation.ResourceId))
            .FirstOrDefaultAsync(ct);
        FunctionPlanningCurrent? current = null;
        if (root is not null)
        {
            var loaded = await ReadCurrentStateAsync(root, session, ct);
            if (loaded is DeclarationReadBlocked blocked)
                return blocked;

            current = (FunctionPlanningCurrent)loaded.Value;
        }
        else if (operation.Phase != DeclarationPhase.Prepare)
        {
            return Blocked(FunctionDeclarationError.PlacementUnavailable);
        }
        else if (await routes.Find(session, FunctionRouteBindingFilters.ByRoute(declaration.FunctionRoute))
                     .AnyAsync(ct))
        {
            return Blocked(FunctionDeclarationError.FunctionRouteUnavailable);
        }

        var references = ImmutableDictionary.CreateBuilder<string, FunctionSecretReference>();
        ConsumerSecretPlanningSnapshot? secrets = null;
        if (operation.Phase == DeclarationPhase.Configure)
        {
            foreach (var (name, reference) in declaration.SecretEnvironment)
            {
                var secretId = await git.ResolveAcceptedAsync(new(context, operation.ResourceId,
                    new("Secret", reference.Secret)), session, ct);
                if (secretId is null)
                    return Blocked(FunctionDeclarationError.SecretReferenceUnavailable);

                references.Add(name, new(secretId, reference.Key));
            }

            var loaded = await loadSecrets(new(context.TenantId, groupId, scope.IsolationScopeId,
                FunctionResourceDeployment.ResourceType, operation.ResourceId, PreviousSecrets(current),
                references.Values.Select(reference => new ConsumerSecretKeyReference(reference.SecretId, reference.Key))
                    .ToArray(), current!.Placements.Items.Select(item => item.DeploymentId).ToArray()), session, ct);
            if (loaded is ConsumerSecretReferenceUnavailable unavailable)
                return new DeclarationReadBlocked(new FunctionDeclarationSecretRejected(unavailable.Reason));

            secrets = (ConsumerSecretPlanningSnapshot)loaded.Value;
        }

        return new FunctionPlanningDependencies(current, scope, target, compute, references.ToImmutable(), secrets);
    }

    private static ConsumerSecretKeyReference[] PreviousSecrets(FunctionPlanningCurrent? current) =>
        current is null ? [] : ConfigurationRevisions(current).SelectMany(revision =>
                revision.Spec.Configuration.SecretReferences.Values)
            .Select(reference => new ConsumerSecretKeyReference(reference.SecretId, reference.Key))
            .Distinct().ToArray();

    private static IEnumerable<FunctionRevision> ConfigurationRevisions(FunctionPlanningCurrent current) =>
        new[] { current.Latest, current.Active }.OfType<FunctionRevision>().DistinctBy(revision => revision.Id);

    private static DeclarationDocumentVersion[] CurrentVersions(FunctionPlanningCurrent current) =>
    [
        new($"FunctionApp/{current.Root.Id}", current.Root.NextRevisionNumber),
        new($"FunctionApp/{current.Root.Id}/PlanningFence", current.Root.PlanningFence),
        new($"FunctionApp/{current.Root.Id}/Managed/{PlanningRootHash(current.Root)}", 0),
        new($"FunctionApp/{current.Root.Id}/Pointers/{current.Root.ActiveRevisionId}/{current.Root.PendingRevisionId}",
            0),
        new($"FunctionRoute/{current.Route.NormalizedRoute}/{current.Route.State}/{current.Route.ActiveRevisionId}",
            current.Route.PlanningFence),
        .. ConfigurationRevisions(current).Select(revision => new DeclarationDocumentVersion(
            $"FunctionRevision/{revision.Id}/{ManagedSpecHash(revision.Spec)}", revision.PlanningFence)),
        .. current.Placements.Items.Select(item =>
            new DeclarationDocumentVersion($"ResourceDeployment/{item.DeploymentId}", item.Revision))
    ];

    private static DeclarationDocumentVersion[] DependencyVersions(FunctionPlanningDependencies dependencies) =>
    [
        new($"ResourceGroup/{dependencies.Scope.ResourceGroupId}", dependencies.Scope.ResourceGroupRevision),
        new($"IsolationScope/{dependencies.Scope.IsolationScopeId}", dependencies.Scope.ScopeRevision),
        new($"DeploymentTarget/{dependencies.Target.Target.Id}", dependencies.Target.Target.Revision),
        new($"DeploymentTarget/{dependencies.Target.Target.Id}/WorkloadConfiguration",
            dependencies.Target.Target.WorkloadConfiguration.Revision),
        new($"ComputeProfile/{dependencies.Compute.Compute.ComputeProfileId}/Policy",
            dependencies.Compute.PolicyRevision),
        .. dependencies.Secrets?.Secrets.SelectMany(version => new DeclarationDocumentVersion[]
        {
            new($"Secret/{version.SecretId}", version.Revision),
            new($"Secret/{version.SecretId}/ReferenceFence", version.ReferenceFence)
        }) ?? []
    ];
}

internal sealed record FunctionPlanningCurrent(
    FunctionApp Root,
    FunctionRevision? Latest,
    FunctionRevision? Active,
    FunctionRouteBinding Route,
    ResourceDeploymentPlanningSnapshot Placements);

internal sealed record FunctionPlanningDependencies(
    FunctionPlanningCurrent? Current,
    IsolationScopePlanningSnapshot Scope,
    DeploymentTargetPlanningSnapshot Target,
    ComputeProfilePlanningSnapshot Compute,
    ImmutableDictionary<string, FunctionSecretReference> SecretReferences,
    ConsumerSecretPlanningSnapshot? Secrets);

internal union ReadFunctionPlanningOutcome(
    FunctionPlanningDependencies,
    DeclarationReadBlocked);

internal union ReadFunctionCurrentOutcome(
    FunctionPlanningCurrent,
    DeclarationReadBlocked);
