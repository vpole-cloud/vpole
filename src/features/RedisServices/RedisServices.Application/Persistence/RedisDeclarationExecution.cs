using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence.Mutations;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using static Pole.Features.RedisServices.Application.Persistence.RedisDeclarations;
using static Pole.Features.RedisServices.Application.Persistence.RedisPlanning;

namespace Pole.Features.RedisServices.Application.Persistence;

internal sealed class RedisDeclarationExecution(
    IMongoCollection<Redis> resources,
    IGitOpsMutations git,
    MongoRunTransaction transactions,
    RedisDeclarationPlanner planner,
    IQuotaAdmissionExecutor quotas,
    IResourceRegistrar registrar,
    CreateResourceDeployment createPlacement,
    RequestResourceDeploymentSetApply applyPlacements,
    PersistCreatedRedis persistCreated,
    PersistUpdatedRedis persistUpdated)
{
    public async Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct)
    {
        if (operation.Phase is not (DeclarationPhase.Prepare or DeclarationPhase.Configure) ||
            operation.Plan.Decision is not (CreateDeclarationPlan or UpdateDeclarationPlan))
            return await BlockAsync(operation,
                new RedisDeclarationRejected(RedisDeclarationError.UnexpectedOperation), ct);

        var loaded = await transactions.InvokeOutcomeAsync<ReadRedisPlanningOutcome>(async (tx, token) =>
            tx.Commit(await planner.ReadPlanningAsync(operation.Input, tx.Session, token)), ct);
        if (loaded is DeclarationReadBlocked blocked)
            return await BlockAsync(operation, blocked.Failure, ct);

        var dependencies = (RedisPlanningDependencies)loaded.Value;
        var current = dependencies.Current;
        var compute = dependencies.Compute.Compute;
        var nextQuota = RedisQuotaInput.Create(compute.Requests.CpuMillis, compute.Requests.MemoryMi);
        var previousCompute = dependencies.Placements?.Items[0].Compute;
        QuotaMutation<RedisQuotaInput> mutation = current is null
            ? new CreateQuotaMutation<RedisQuotaInput>(nextQuota)
            : new UpdateQuotaMutation<RedisQuotaInput>(RedisQuotaInput.Create(
                previousCompute!.Requests.CpuMillis, previousCompute.Requests.MemoryMi), nextQuota);
        var result = await quotas.ExecuteOutcomeAsync<RedisQuotaInput, GitOperationResult>(
            new ExecuteQuotaAdmissionRequest<RedisQuotaInput, GitOperationResult>(
                TenantId: operation.Context.TenantId,
                ResourceKey: RedisQuotaResourceKey.Create(operation.ResourceId),
                Mutation: mutation,
                QuotaError: _ => new(operation.ResourceId,
                    new RedisDeclarationRejected(RedisDeclarationError.QuotaUnavailable)),
                MutationCallback: async (tx, token) =>
                {
                    var rejected = await git.BeginAsync(operation, tx.Session, token);
                    if (rejected is not null)
                        return tx.Abort(rejected);

                    // Begin increments the technical fence; retain that authoritative value on replacement.
                    var authoritative = await resources.Find(tx.Session,
                        RedisFilters.ById(operation.Context.TenantId, operation.ResourceId)).FirstOrDefaultAsync(token);
                    if (authoritative?.Revision != current?.Revision)
                        return tx.Abort(new(operation.ResourceId, new DeclarationPreconditionsChanged()));

                    var execution = new RedisExecution(operation, dependencies with { Current = authoritative });
                    var outcome = operation.Phase == DeclarationPhase.Prepare && authoritative is null
                        ? await PrepareAsync(execution, tx.Session, token)
                        : operation.Phase == DeclarationPhase.Configure && authoritative is not null
                            ? await ConfigureAsync(execution, tx.Session, token)
                            : new GitOperationResult(operation.ResourceId, new DeclarationPreconditionsChanged());
                    if (!outcome.Applied)
                        return tx.Abort(outcome);

                    var completed = await git.CompleteAsync(new(operation, outcome), tx.Session, token);
                    return completed.Applied ? tx.Commit(completed) : tx.Abort(completed);
                }), cancellationToken: ct);

        return result.Applied || result.Failure is DeclarationPreconditionsChanged or StaleGitExecution
            ? result : await BlockAsync(operation, result.Failure!, ct);
    }

    private async Task<GitOperationResult> PrepareAsync(
        RedisExecution execution,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var (operation, dependencies) = execution;
        var spec = DeclarationJson.Read<RedisDeclaration>(operation.Declaration.Spec);
        var resource = RedisFactory.Create(operation.Context.TenantId, operation.Declaration.Address.Name,
            dependencies.Scope.ResourceGroupId,
            new RedisSpec(new Storage(spec.Storage.SizeGb, dependencies.Storage.Storage.ProfileId)),
            RedisFactory.DefaultCreateOptions with
            {
                Id = operation.ResourceId,
                Tags = spec.Tags.ToImmutableHashSet(StringComparer.Ordinal),
                Lifecycle = new PreparedRedisLifecycle()
            });
        if (await persistCreated(session, resource, ct) is RedisServiceNameAlreadyExists)
            return new(resource.Id, new RedisDeclarationRejected(RedisDeclarationError.ExplicitAdoptionRequired));

        if (await registrar.RegisterAsync(new(resource.TenantId, resource.ResourceGroupId,
                new(RedisResourceDeployment.ResourceType, resource.Id, resource.Name)), session, ct)
            is ResourceRegistrationMutationError)
            return new(resource.Id,
                new RedisDeclarationRejected(RedisDeclarationError.ResourceRegistrationUnavailable));

        var target = dependencies.Target.Target;
        var created = await createPlacement(new(ObjectId.GenerateNewId().ToString(), resource.TenantId,
                RedisResourceDeployment.ResourceType, resource.Id, resource.ResourceGroupId,
                dependencies.Scope.IsolationScopeId, new(target.Id, target.WorkloadConfiguration.Revision, null),
                dependencies.Compute.Compute.ToResourceDeploymentConfiguration(),
                StorageConfiguration(dependencies.Storage.Storage), ResourcePlacementCapability.SingleTarget,
                DeferredActivation: true, PinnedCompute: true,
                StoragePolicyRevision: dependencies.Storage.PolicyRevision),
            session, ct);
        return created is PlacementSummaryDto
            ? new(resource.Id, Changed: true)
            : new(resource.Id, new RedisDeclarationRejected(RedisDeclarationError.PlacementUnavailable));
    }

    private async Task<GitOperationResult> ConfigureAsync(
        RedisExecution execution,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var (operation, dependencies) = execution;
        var current = dependencies.Current!;
        var spec = DeclarationJson.Read<RedisDeclaration>(operation.Declaration.Spec);
        var next = current.ActivatePrepared(spec.Tags.ToImmutableHashSet(StringComparer.Ordinal));
        var placement = dependencies.Placements!.Items[0];
        var applied = await applyPlacements(new(current.TenantId, RedisResourceDeployment.ResourceType,
                current.Id, next.Revision,
                [new(placement.DeploymentId, placement.Revision,
                    new(placement.Compute.ComputeProfileId, placement.Compute.ComputeProfileRevision),
                    new PreserveIngress(), dependencies.Compute.Compute.ToResourceDeploymentConfiguration(),
                    FixedReplicas: 1, PinnedCompute: true)]), session, ct);
        if (applied is ResourceDeploymentMutationError
            { Value: ResourceDeploymentChanged or ResourceDeploymentComputeConfigurationChanged })
            return new(current.Id, new DeclarationPreconditionsChanged());

        if (applied is not MutatedResourceDeployments)
            return new(current.Id, new RedisDeclarationRejected(RedisDeclarationError.PlacementUnavailable));

        return await persistUpdated(new(current, next, operation.Context), session, ct) switch
        {
            RedisUpdated => new(current.Id, Changed: true),
            RedisUpdateConflict => new(current.Id, new DeclarationPreconditionsChanged()),
            RedisGitOwnedConfiguration => new(current.Id, new GitDeclarationOwnershipConflict())
        };
    }

    private Task<GitOperationResult> BlockAsync(
        GitOperation operation,
        IDeclarationFailure failure,
        CancellationToken ct) =>
        git.ExecuteAsync(operation,
            (_, _) => Task.FromResult(new GitOperationResult(operation.ResourceId, failure)), ct);

    private sealed record RedisExecution(
    GitOperation Operation,
    RedisPlanningDependencies Dependencies);
}
