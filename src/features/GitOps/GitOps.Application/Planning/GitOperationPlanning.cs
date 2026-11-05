using Pole.Features.GitOps.Application.Mongo;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application.Planning;

public sealed record GitOperationPlanned(GitOperation Operation);

public sealed record GitOperationReplayed(GitOperationResult Result);

public union PlanGitOperationOutcome(
    GitOperationPlanned,
    GitOperationReplayed,
    StaleGitExecution);

public sealed class GitOperationPlanning(
    IMongoCollection<GitPlannedOperation> plans,
    IMongoCollection<ManagedDeclaration> inventory,
    IMongoCollection<GitOperationReceipt> receipts,
    GitRunStore runs,
    IServiceProvider services,
    MongoRunTransaction transactions,
    TimeProvider clock)
{
    private DeclarativeResourcePlanner Planner => services.GetRequiredService<DeclarativeResourcePlanner>();
    private DeclarativeProviderRegistry Providers => services.GetRequiredService<DeclarativeProviderRegistry>();

    public async Task<PlanGitOperationOutcome> PlanAsync(GitOperationInput input, CancellationToken ct)
    {
        IDeclarationFailure? preparationFailure = null;
        if (Providers.Find(input.Declaration.Address.Kind) is IDeclarativeReferencePreparation preparation)
        {
            var prepared = await preparation.PrepareReferencesAsync(input, ct);
            if (prepared is StaleGitExecution stale)
                return stale;
            if (prepared is DeclarationReadBlocked blocked)
                preparationFailure = blocked.Failure;
        }

        return await transactions.InvokeOutcomeAsync<PlanGitOperationOutcome>(async (tx, token) =>
        {
            var run = await runs.TouchAsync(input.Context.ToHandle(), tx.Session, token);
            if (!GitOperationAuthority.Matches(run, input))
                return tx.Abort(new StaleGitExecution());

            var id = GitOperationReceipts.ReceiptId(input);
            var receipt = await receipts.Find(tx.Session, GitOperationReceiptFilters.Identity(id))
                .FirstOrDefaultAsync(token);
            if (receipt is { Blocker: null } && receipt.ResourceId == input.ResourceId &&
                receipt.DesiredHash == input.Declaration.Hash && receipt.Fence == input.Context.Fence)
                return tx.Commit(new GitOperationReplayed(new(receipt.ResourceId)));

            var owner = await LoadOwnerAsync(input, tx.Session, token);
            var dependency = preparationFailure ??
                await DependencyFailureAsync(input, run!.AcceptedSnapshot!, tx.Session, token);
            var decision = dependency is not null
                ? new BlockedDeclarationPlan(dependency)
                : Providers.Find(input.Declaration.Address.Kind) is ITransactionalDeclarativeResourceProvider
                    ? await Planner.PlanAsync(Request(input, owner), tx.Session, token)
                    : new BlockedDeclarationPlan(new DeclarationPlanningUnsupported(input.Declaration.Address.Kind));
            var previous = await plans.Find(tx.Session, GitOperationPlanFilters.Identity(id))
                .FirstOrDefaultAsync(token);
            var value = ToDecision(input, owner, decision);
            var now = clock.GetUtcNow().UtcDateTime;
            var stored = previous is null
                ? GitPlannedOperationFactory.Create(new(input.Context.ToHandle().Stamp,
                    new(input.Declaration.Address.Kind, input.Declaration.Address.Name, input.ResourceId), input.Phase),
                    value, now)
                : GitPlannedOperationFactory.Replan(previous, value, now);
            await plans.ReplaceOneAsync(tx.Session, GitOperationPlanFilters.Identity(id), stored,
                new ReplaceOptions { IsUpsert = true }, token);

            return tx.Commit(new GitOperationPlanned(new(input, new(stored.Revision, decision))));
        }, ct);
    }

    private async Task<IDeclarationFailure?> DependencyFailureAsync(GitOperationInput input,
        GitAcceptedSnapshot accepted, IClientSessionHandle session, CancellationToken ct)
    {
        foreach (var dependency in input.Declaration.Steps.Single(step => step.Phase == input.Phase).Dependencies)
        {
            var resource = accepted.Resources.SingleOrDefault(item =>
                item.Kind == dependency.Resource.Kind && item.Name == dependency.Resource.Name);
            if (resource is null)
                continue;

            var key = GitOperationReceipts.ReceiptId(input with
            {
                Declaration = input.Declaration with { Address = dependency.Resource },
                ResourceId = resource.ResourceId,
                Phase = dependency.Phase
            });
            var receipt = await receipts.Find(session, GitOperationReceiptFilters.Identity(key))
                .FirstOrDefaultAsync(ct);
            if (receipt is not { Blocker: null } || receipt.Fence != input.Context.Fence ||
                receipt.ResourceId != resource.ResourceId || receipt.DesiredHash != resource.Hash)
                return new DeclarationDependencyBlocked(dependency.Resource, dependency.Phase);
        }

        return null;
    }

    internal async Task<IDeclarationFailure?> ValidateAsync(GitOperation operation,
        IClientSessionHandle session, CancellationToken ct)
    {
        var stored = await plans.Find(session,
                GitOperationPlanFilters.Identity(GitOperationReceipts.ReceiptId(operation)))
            .FirstOrDefaultAsync(ct);
        var owner = await LoadOwnerAsync(operation.Input, session, ct);
        if (stored is null || stored.Revision != operation.Plan.Revision ||
            stored.Stamp.Run != operation.Context.ToHandle().Stamp ||
            stored.Stamp.Resource.ResourceId != operation.ResourceId ||
            !stored.Decision.ToBsonDocument().Equals(
                ToDecision(operation.Input, owner, operation.Plan.Decision).ToBsonDocument()))
            return new DeclarationPreconditionsChanged();

        if (DeclarationPlanValues.Blocker(operation.Plan.Decision) is not null)
            return null;

        var current = await Planner.PlanAsync(Request(operation.Input, owner), session, ct);
        if (!DeclarationPlanValues.Same(operation.Plan.Decision, current))
            return new DeclarationPreconditionsChanged();

        if (Providers.Find(operation.Declaration.Address.Kind) is not
            ITransactionalDeclarativeResourceProvider provider)
            return new DeclarationPlanningUnsupported(operation.Declaration.Address.Kind);

        var fenced = await provider.FenceAsync(new(operation.Input, DeclarationPlanValues.Versions(current)),
            session, ct);
        return fenced switch
        {
            DeclarationFenced => null,
            DeclarationPreconditionsChanged changed => changed,
            DeclarationReadBlocked blocked => blocked.Failure
        };
    }

    internal async Task<ManagedDeclaration?> LoadOwnerAsync(GitOperationInput operation,
        IClientSessionHandle session, CancellationToken ct) =>
        await inventory.Find(session, ManagedDeclarationFilters.Address(operation.Context.TenantId,
            operation.Declaration.Address)).FirstOrDefaultAsync(ct);

    private static PlanDeclarationRequest Request(GitOperationInput input, ManagedDeclaration? owner) =>
        new(new(input.Context.TenantId, input.Context.SourceId, input.ResourceId,
                new(input, owner?.SourceId, owner?.Materialization ?? GitResourceMaterialization.Allocated)),
            input.Declaration);

    private static GitPlanDecision ToDecision(GitOperationInput input, ManagedDeclaration? owner,
        ResourceDeclarationPlan decision)
    {
        var current = DeclarationPlanValues.Current(decision);
        var desired = DeclarationPlanValues.Desired(decision);
        var desiredJson = desired is null ? null : DeclarationPlanValues.Json(desired);
        return new(DeclarationPlanValues.Action(decision),
            current is null ? null : new(
                new(input.Declaration.Address.Kind, input.Declaration.Address.Name, current.ResourceId),
                owner?.SourceId,
                current.Version.Documents.Select(item =>
                    new GitResourceVersion(item.DocumentId, item.Revision)).ToImmutableArray(),
                DeclarationPlanValues.Hash(DeclarationPlanValues.Json(current.Projection))),
            desiredJson is null ? null : new(DeclarationPlanValues.Hash(desiredJson), desiredJson),
            (desired?.Dependencies?.Documents ?? []).Select(item =>
                new GitResourceVersion(item.DocumentId, item.Revision)).ToImmutableArray(),
            DeclarationPlanValues.ChangedFields(decision).ToImmutableArray());
    }
}
