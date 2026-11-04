using Pole.Features.GitOps.Domain;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.GitOps.Application.Persistence;

internal sealed class MongoGitOpsMutations(
    IMongoCollection<GitSourceExecution> sources,
    MongoRunTransaction transactions,
    MongoGitOwnership ownership,
    GitDeclarationCompletion completion,
    GitOperationReceipts receipts,
    GitOwnershipStatuses statuses) : IGitOpsMutations
{
    public async Task<GitOperationResult> ExecuteAsync(GitOperation operation,
        Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation, CancellationToken ct)
    {
        async Task<GitOperationResult> AttemptAsync() =>
            await transactions.InvokeOutcomeAsync<GitOperationResult>(async (
                    tx,
                    token) =>
                {
                    var rejected = await BeginAsync(operation, tx.Session, token);

                    if (rejected is not null)
                        return tx.Abort(rejected);

                    var result = await mutation(tx.Session, token);

                    if (!result.Applied)
                        return tx.Abort(result);

                    await CompleteAsync(
                        new CompleteGitOperationRequest(
                            Operation: operation,
                            Result: result),
                        session: tx.Session,
                        ct: token);

                    return tx.Commit(result);
                }, ct);
        GitOperationResult outcome;
        try { outcome = await AttemptAsync(); }
        catch (MongoWriteException ex) when (MongoDuplicateKey.IsForIdIndex(ex, GitOpsCollections.Inventory) ||
        MongoDuplicateKey.IsForIndex(ex, GitOpsIndexesMigration.ResourceIdentityIndex, GitOpsCollections.Inventory))
        { outcome = await AttemptAsync(); }
        // A blocked provider operation may have staged writes; abort all of them first. Retain only
        // its sanitized diagnostic in a fresh fenced transaction, along with the stable address.
        if (!outcome.Applied && outcome.Failure is not StaleGitExecution and not GitDeclarationOwnershipConflict)
            await transactions.InvokeOutcomeAsync<GitOperationResult>(async (tx, token) =>
                {
                    var rejected = await BeginAsync(operation, tx.Session, token);

                    if (rejected is not null)
                        return tx.Abort(rejected);

                    await CompleteAsync(
                        new CompleteGitOperationRequest(
                            Operation: operation,
                            Result: outcome),
                        session: tx.Session,
                        ct: token);

                    return tx.Commit(outcome);
                }, ct);

        if (outcome.Failure is GitDeclarationOwnershipConflict)
            await transactions.InvokeOutcomeAsync<bool>(async (tx, token) =>
                {
                    // Keep the rejection on the requesting run; touching the other owner's inventory
                    // would either hide its successful state or imply that this source claimed it.
                    var fenced = await sources.UpdateOneAsync(tx.Session,
                        GitSourceExecutionFilters.Run(operation.Context),
                        Builders<GitSourceExecution>.Update.Inc(
                            item => item.MutationFence,
                            1), cancellationToken: token);

                    if (fenced.ModifiedCount != 1)
                        return tx.Abort(false);

                    await receipts.StoreAsync(operation, outcome, tx.Session, token);

                    return tx.Commit(true);
                }, ct);

        return outcome;
    }

    public Task<GitOperationResult?> BeginAsync(
        GitOperation operation,
        IClientSessionHandle session,
        CancellationToken ct) =>
        ownership.BeginAsync(operation, session, ct);

    public Task CompleteAsync(
        CompleteGitOperationRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        completion.StoreAsync(request.Operation, request.Result, session, ct);

    public Task<bool> AuthorizeAsync(
        AuthorizeGitResourceMutationRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        ownership.AuthorizeAsync(request.TenantId, request.Kind, request.ResourceId, session, ct, request.Context);

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
