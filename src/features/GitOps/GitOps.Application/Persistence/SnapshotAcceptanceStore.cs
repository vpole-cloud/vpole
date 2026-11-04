using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

public delegate Task<SnapshotAcceptance> AcceptSnapshot(
    CompiledSnapshot snapshot,
    GitMutationContext context,
    string commit,
    long expectedFence,
    IReadOnlyList<string> referencedSecrets,
    CancellationToken cancellationToken);

internal sealed class SnapshotAcceptanceStore(
    IMongoCollection<GitSourceExecution> sources,
    IMongoCollection<ManagedDeclaration> inventory,
    MongoRunTransaction transactions,
    TimeProvider clock)
{
    public async Task<SnapshotAcceptance> InvokeAsync(CompiledSnapshot snapshot, GitMutationContext context,
        string commit, long expectedFence, IReadOnlyList<string> referencedSecrets, CancellationToken ct)
    {
        async Task<SnapshotAcceptance> AcceptAsync() =>
            await transactions.InvokeOutcomeAsync<SnapshotAcceptance>(async (
                    tx,
                    token) =>
                {
                    var previous = await sources.Find(
                        tx.Session,
                        GitSourceExecutionFilters.Source(context.TenantId, context.SourceId))
                        .FirstOrDefaultAsync(token);
                    var acceptance = previous is null
                    ? expectedFence == 0 ? SnapshotAcceptance.Accepted : SnapshotAcceptance.StaleExecution
                    : previous.ToDomain().EvaluateAcceptance(expectedFence, context.SourceUrl);

                    if (acceptance != SnapshotAcceptance.Accepted)
                        return tx.Abort(acceptance);

                    var allocated = new List<AcceptedDeclaration>();
                    foreach (var item in snapshot.Declarations)
                    {
                        var existing = await inventory.Find(
                            tx.Session,
                            ManagedDeclarationFilters.Address(context.TenantId, item.Address))
                            .FirstOrDefaultAsync(token);
                        allocated.Add(new(
                                item.Address.Kind,
                                item.Address.Name,
                                item.Hash,
                                item.Spec.GetRawText(),
                                existing?.ResourceId ?? ObjectId.GenerateNewId().ToString()));
                    }
                    var execution = SnapshotExecutionFactory.Create(context.ToStamp(), context.SourceUrl, commit,
                        previous?.AppliedCommit, allocated.Select(item => item.ToBinding()), new ApplyingSnapshot());
                    var source = new GitSourceExecution(GitSourceExecutionFilters.SourceId(
                            context.TenantId,
                            context.SourceId),
                        context.TenantId, context.SourceId, context.Fence, 0, context.RunId, commit,
                        execution.AttemptedCommit,
                        execution.AppliedCommit, execution.Progress,
                        snapshot.Declarations.Select(item => ManagedDeclarationFilters.AddressId(
                                context.TenantId,
                                item.Address)).ToImmutableArray(),
                        AcceptedSnapshotCodec.Write(allocated), context.SourceUrl, context.ActorUserId,
                        context.ActorEmail,
                        SnapshotSchema.ApiVersion, referencedSecrets.ToImmutableArray(),
                        NextAttemptAtUtc: clock.GetUtcNow().UtcDateTime,
                        Attempt: 0L);

                    if (previous is null)
                        await sources.InsertOneAsync(tx.Session, source, cancellationToken: token);
                    else await sources.ReplaceOneAsync(
                            tx.Session,
                            GitSourceExecutionFilters.Source(context.TenantId, context.SourceId),
                            source,
                            cancellationToken: token);

                    var oldInventory = await inventory.Find(
                        tx.Session,
                        ManagedDeclarationFilters.SourceInventory(context.TenantId, context.SourceId))
                        .ToListAsync(token);
                    foreach (var retained in oldInventory.Where(item => !source.DesiredAddresses.Contains(item.Id)))
                    {
                        var removal = retained.ToDomain().RetainPendingRemoval();
                        await inventory.UpdateOneAsync(tx.Session, ManagedDeclarationFilters.Address(
                                context.TenantId,
                                new(retained.Kind, retained.Name)),
                            Builders<ManagedDeclaration>.Update.Set(
                                item => item.PendingRemoval,
                                removal.PendingRemoval), cancellationToken: token);
                    }

                    return tx.Commit(SnapshotAcceptance.Accepted);
                }, ct);
        try { return await AcceptAsync(); }
        catch (MongoWriteException ex) when (MongoDuplicateKey.IsForIdIndex(ex, GitOpsCollections.Executions))
        { return SnapshotAcceptance.StaleExecution; }
    }
}
