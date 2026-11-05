using Pole.Features.GitOps.Application.Mongo;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application.Inventory;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Planning;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Runs;

internal sealed class MongoGitOpsMutations(
    GitRunStore runs,
    GitAcceptedReferences references,
    MongoRunTransaction transactions,
    MongoGitOwnership ownership,
    MongoGitLifecycleGuard lifecycle,
    GitDeclarationCompletion completion,
    GitOperationReceipts receipts,
    GitOwnershipStatuses statuses) : IGitOpsMutations
{
    public async Task<GitOperationResult> ExecuteAsync(GitOperation operation,
        Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation,
        CancellationToken ct)
    {
        async Task<GitOperationResult> AttemptAsync() =>
            await transactions.InvokeOutcomeAsync<GitOperationResult>(async (
                    tx,
                    token) =>
                {
                    var rejected = await BeginAsync(operation, tx.Session, token);

                    if (rejected is not null)
                        return rejected.Applied ? tx.Commit(rejected) : tx.Abort(rejected);

                    var result = await mutation(tx.Session, token);

                    if (!result.Applied)
                        return tx.Abort(result);

                    var completed = await CompleteAsync(
                        new CompleteGitOperationRequest(
                            Operation: operation,
                            Result: result),
                        session: tx.Session,
                        ct: token);

                    return completed.Applied ? tx.Commit(result) : tx.Abort(completed);
                },
                ct);

        GitOperationResult outcome;
        try
        {
            outcome = await AttemptAsync();
        }
        catch (MongoWriteException ex) when (MongoDuplicateKey.IsForIdIndex(ex, GitOpsCollections.Inventory) ||
                                             MongoDuplicateKey.IsForIndex(ex,
                                                 GitOpsIndexes.ResourceIdentity,
                                                 GitOpsCollections.Inventory))
        {
            outcome = await AttemptAsync();
        }

        // A blocked provider operation may have staged writes; abort all of them first. Retain only
        // its sanitized diagnostic in a fresh fenced transaction, along with the stable address.
        if (!outcome.Applied && outcome.Failure is not StaleGitExecution and not GitDeclarationOwnershipConflict
            and not DeclarationPreconditionsChanged)
            await transactions.InvokeOutcomeAsync<GitOperationResult>(async (tx, token) =>
                {
                    var rejected = await BeginAsync(operation, tx.Session, token);

                    if (rejected is not null)
                        return rejected.Applied ? tx.Commit(rejected) : tx.Abort(rejected);

                    var completed = await CompleteAsync(
                        new CompleteGitOperationRequest(
                            Operation: operation,
                            Result: outcome),
                        session: tx.Session,
                        ct: token);

                    return completed.Failure is StaleGitExecution ? tx.Abort(completed) : tx.Commit(outcome);
                },
                ct);

        if (outcome.Failure is GitDeclarationOwnershipConflict)
            await transactions.InvokeOutcomeAsync<bool>(async (tx, token) =>
                {
                    // Keep the rejection on the requesting run; touching the other owner's inventory
                    // would either hide its successful state or imply that this source claimed it.
                    if (await runs.TouchAsync(operation.Context.ToHandle(), tx.Session, token) is null)
                        return tx.Abort(false);

                    await receipts.StoreAsync(operation, outcome, tx.Session, token);

                    return tx.Commit(true);
                },
                ct);

        return outcome;
    }

    public Task<GitOperationResult?> BeginAsync(
        GitOperation operation,
        IClientSessionHandle session,
        CancellationToken ct) =>
        ownership.BeginAsync(operation, session, ct);

    public async Task<GitOperationResult> CompleteAsync(
        CompleteGitOperationRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (await runs.TouchAsync(request.Operation.Context.ToHandle(), session, ct) is null)
            return new(request.Operation.ResourceId, new StaleGitExecution());

        return await completion.StoreAsync(request.Operation, request.Result, session, ct);
    }

    public Task<string?> ResolveAcceptedAsync(
        ResolveAcceptedGitReferenceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => references.ResolveAsync(request, session, ct);

    public Task<PinGitReferenceOutcome> PinReferenceAsync(PinGitReferenceRequest request, CancellationToken ct) =>
        references.PinAsync(request, ct);

    public Task<bool> AuthorizeAsync(
        AuthorizeGitResourceMutationRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        request.Lifecycle is not null
            ? lifecycle.AuthorizeAsync(request, session, ct)
            : ownership.AuthorizeAsync(request.TenantId, request.Kind, request.ResourceId, session, ct,
                request.Context);

    public Task<string?> ResolveAsync(
        ResolveGitResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        ownership.ResolveAsync(request.TenantId, request.Address, session, ct);

    public Task<IReadOnlyDictionary<string, GitResourceStatus>> StatusesAsync(
        LoadGitResourceStatusesRequest request,
        CancellationToken ct) => statuses.StatusesAsync(request.TenantId, request.Kind, request.ResourceIds, ct);

    public Task<GitResourceStatus?> StatusAsync(
        LoadGitResourceStatusRequest request,
        CancellationToken ct) =>
        statuses.StatusAsync(request.TenantId, request.Kind, request.ResourceId, ct);
}
