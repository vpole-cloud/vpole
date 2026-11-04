using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Workloads;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Functions.Application.Persistence;

internal sealed partial class DeclarativeFunctionProvider
{
    public async Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct)
    {
        if (operation.Phase is not (DeclarationPhase.Prepare or DeclarationPhase.Configure) ||
            operation.Plan.Decision is not (CreateDeclarationPlan or UpdateDeclarationPlan))
            return await BlockAsync(operation,
                new FunctionDeclarationRejected(FunctionDeclarationError.UnexpectedOperation), ct);

        var loaded = await transactions.InvokeOutcomeAsync<ReadFunctionPlanningOutcome>(async (tx, token) =>
            tx.Commit(await ReadPlanningAsync(operation.Input, tx.Session, token)), ct);
        if (loaded is DeclarationReadBlocked blocked)
            return await BlockAsync(operation, blocked.Failure, ct);

        var dependencies = (FunctionPlanningDependencies)loaded.Value;
        if (operation.Phase == DeclarationPhase.Configure && dependencies.Current?.Root.PendingRevisionId is not null)
            return await BlockAsync(operation,
                new FunctionDeclarationRejected(FunctionDeclarationError.PendingRevision), ct);

        var declaration = FunctionDeclarationJson.Read(operation.Declaration.Spec);
        var spec = FunctionDeclarationJson.Spec(declaration, dependencies.SecretReferences, null);
        var nextQuota = operation.Phase == DeclarationPhase.Prepare ? new FunctionQuotaAllocation(1, 0, 0, 0)
            : FunctionQuotaAllocation.Peak(FunctionQuotaAllocation.FromRevision(spec),
                dependencies.Current?.Active is { } active
                    ? FunctionQuotaAllocation.FromRevision(active.Spec) : new(1, 0, 0, 0));
        QuotaMutation<FunctionQuotaAllocation> mutation = dependencies.Current is { } current
            ? new UpdateQuotaMutation<FunctionQuotaAllocation>(current.Root.AdmittedQuota, nextQuota)
            : new CreateQuotaMutation<FunctionQuotaAllocation>(nextQuota);
        GitOperationResult result;
        try
        {
            result = await quotas.ExecuteOutcomeAsync<FunctionQuotaAllocation, GitOperationResult>(
                new ExecuteQuotaAdmissionRequest<FunctionQuotaAllocation, GitOperationResult>(
                    TenantId: operation.Context.TenantId,
                    ResourceKey: FunctionAppQuotaResourceKey.Create(operation.ResourceId),
                    Mutation: mutation,
                    QuotaError: _ => new(operation.ResourceId,
                        new FunctionDeclarationRejected(FunctionDeclarationError.QuotaUnavailable)),
                    MutationCallback: async (tx, token) =>
                    {
                        var rejected = await git.BeginAsync(operation, tx.Session, token);
                        if (rejected is not null)
                            return tx.Abort(rejected);

                        var root = await resources.Find(tx.Session,
                            FunctionAppFilters.ByTenantAndId(operation.Context.TenantId, operation.ResourceId))
                            .FirstOrDefaultAsync(token);
                        if (root?.NextRevisionNumber != dependencies.Current?.Root.NextRevisionNumber)
                            return tx.Abort(new(operation.ResourceId, new DeclarationPreconditionsChanged()));

                        var authoritative = dependencies;
                        if (root is not null)
                        {
                            var state = await ReadCurrentStateAsync(root, tx.Session, token);
                            if (state is DeclarationReadBlocked unavailable)
                                return tx.Abort(new(operation.ResourceId, unavailable.Failure));

                            authoritative = dependencies with { Current = (FunctionPlanningCurrent)state.Value };
                        }

                        var execution = new FunctionExecution(operation, authoritative, nextQuota);
                        var outcome = operation.Phase == DeclarationPhase.Prepare && root is null
                            ? await PrepareAsync(execution, tx.Session, token)
                            : operation.Phase == DeclarationPhase.Configure && root is not null
                                ? await ConfigureAsync(execution, tx.Session, token)
                                : new GitOperationResult(operation.ResourceId, new DeclarationPreconditionsChanged());
                        if (!outcome.Applied)
                            return tx.Abort(outcome);

                        var completed = await git.CompleteAsync(new(operation, outcome), tx.Session, token);
                        return completed.Applied ? tx.Commit(completed) : tx.Abort(completed);
                    }), cancellationToken: ct);
        }
        catch (Exception exception) when (MongoDuplicateKey.IsForIndex(exception,
            FunctionAppIndexes.TenantName, resources.CollectionNamespace.CollectionName) ||
            MongoDuplicateKey.IsForIdIndex(exception, routes.CollectionNamespace.CollectionName))
        {
            return await BlockAsync(operation,
                new FunctionDeclarationRejected(FunctionDeclarationError.ExplicitAdoptionRequired), ct);
        }

        return result.Applied || result.Failure is DeclarationPreconditionsChanged or StaleGitExecution
            ? result : await BlockAsync(operation, result.Failure!, ct);
    }

    private async Task<GitOperationResult> PrepareAsync(
        FunctionExecution execution, IClientSessionHandle session, CancellationToken ct)
    {
        var (operation, dependencies, _) = execution;
        var declaration = FunctionDeclarationJson.Read(operation.Declaration.Spec);
        if (await registrar.RegisterAsync(new(operation.Context.TenantId, dependencies.Scope.ResourceGroupId,
                new(FunctionResourceDeployment.ResourceType, operation.ResourceId, operation.Declaration.Address.Name)),
                session, ct) is ResourceRegistrationMutationError)
            return new(operation.ResourceId,
                new FunctionDeclarationRejected(FunctionDeclarationError.ResourceRegistrationUnavailable));

        var target = dependencies.Target.Target;
        var created = await createPlacement(new(ObjectId.GenerateNewId().ToString(), operation.Context.TenantId,
            FunctionResourceDeployment.ResourceType, operation.ResourceId, dependencies.Scope.ResourceGroupId,
            dependencies.Scope.IsolationScopeId, new(target.Id, target.WorkloadConfiguration.Revision, null),
            dependencies.Compute.Compute.ToFunctionResourceDeploymentConfiguration(declaration.Resources),
            Storage: null, ResourcePlacementCapability.SingleTarget, DeferredActivation: true, PinnedCompute: true),
            session, ct);
        if (created is not PlacementSummaryDto placement)
            return new(operation.ResourceId,
                new FunctionDeclarationRejected(FunctionDeclarationError.PlacementUnavailable));

        var now = clock.GetUtcNow();
        var app = FunctionAppFactory.CreatePrepared(operation.Context.TenantId, dependencies.Scope.ResourceGroupId,
            new(placement.DeploymentId, placement.RuntimeCellId, placement.DeploymentTargetId),
            operation.Declaration.Address.Name, declaration.FunctionRoute, now,
            FunctionAppFactory.DefaultCreateOptions with { FunctionAppId = operation.ResourceId });
        await routes.InsertOneAsync(session, FunctionRouteBindingFactory.Reserve(app, now), cancellationToken: ct);
        await resources.InsertOneAsync(session, app, cancellationToken: ct);
        return new(app.Id, Changed: true);
    }

    private async Task<GitOperationResult> ConfigureAsync(
        FunctionExecution execution, IClientSessionHandle session, CancellationToken ct)
    {
        var (operation, dependencies, quota) = execution;
        var current = dependencies.Current!;
        var root = current.Root;
        if (root.PendingRevisionId is not null)
            return new(root.Id, new DeclarationPreconditionsChanged());

        var declaration = FunctionDeclarationJson.Read(operation.Declaration.Spec);
        var spec = FunctionDeclarationJson.Spec(declaration, dependencies.SecretReferences, current.Latest?.Spec.Image);
        spec = spec with
        {
            Handlers = FunctionTriggerReconciler.Reconcile(current.Latest?.Spec.Handlers ?? [], spec.Handlers)
        };
        var admitted = await admitSecrets(new(root.TenantId, root.ResourceGroupId, dependencies.Scope.IsolationScopeId,
            FunctionResourceDeployment.ResourceType, root.Id, PreviousSecrets(current),
            FunctionSecretReferences.From(spec.Configuration), ConsumerSecretAvailabilityPolicy.RequireAvailable,
            PreviousSecretReferenceRetention.KeepUntilRolloutConfirmed), session, ct);
        if (admitted is ConsumerSecretReferenceUnavailable unavailable)
            return new(root.Id, new FunctionDeclarationSecretRejected(unavailable.Reason));

        var now = clock.GetUtcNow();
        var revision = FunctionRevision.CreateProvisioning(ObjectId.GenerateNewId().ToString(), root.TenantId,
            root.Id, root.NextRevisionNumber, spec, now);
        var persisted = await persistRevision(new(root, revision, quota,
            declaration.Tags.ToImmutableHashSet(StringComparer.Ordinal), now, operation.Context), session, ct);
        if (persisted is FunctionAppGitOwnedConfiguration)
            return new(root.Id, new GitDeclarationOwnershipConflict());

        if (persisted is not FunctionRevision)
            return new(root.Id, new DeclarationPreconditionsChanged());

        var placement = current.Placements.Items[0];
        var applied = await applyPlacements(new(root.TenantId, FunctionResourceDeployment.ResourceType, root.Id,
            revision.RevisionNumber,
            [new(placement.DeploymentId, placement.Revision,
                new(placement.Compute.ComputeProfileId, placement.Compute.ComputeProfileRevision),
                new PreserveIngress(),
                dependencies.Compute.Compute.ToFunctionResourceDeploymentConfiguration(spec.Resources),
                FixedReplicas: 1, PinnedCompute: true)]), session, ct);
        return applied switch
        {
            MutatedResourceDeployments => new(root.Id, Changed: true),
            ResourceDeploymentMutationError
            {
                Value: ResourceDeploymentChanged or
                ResourceDeploymentComputeConfigurationChanged
            } => new(root.Id, new DeclarationPreconditionsChanged()),
            _ => new(root.Id, new FunctionDeclarationRejected(FunctionDeclarationError.PlacementUnavailable))
        };
    }

    private Task<GitOperationResult> BlockAsync(
        GitOperation operation, IDeclarationFailure failure, CancellationToken ct) =>
        git.ExecuteAsync(operation,
            (_, _) => Task.FromResult(new GitOperationResult(operation.ResourceId, failure)), ct);

    private sealed record FunctionExecution(
        GitOperation Operation,
        FunctionPlanningDependencies Dependencies,
        FunctionQuotaAllocation Quota);
}
