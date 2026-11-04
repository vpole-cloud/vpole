using Pole.Database;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.GitOps.Application.Persistence;

public sealed class ManualSnapshotSources(
    IMongoCollection<ManualSnapshotSource> sources,
    IMongoCollection<GitSourceExecution> executions,
    IMongoCollection<ManagedDeclaration> inventory,
    IMongoCollection<GitOperationReceipt> receipts,
    TimeProvider clock)
{
    public static bool ValidUrl(string? url) => url is { Length: > 0 and <= SnapshotHttpLimits.MaximumSourceUrlLength } &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    public async Task<CreateSnapshotSourceResult> CreateAsync(string tenant, Guid creationKey, string url, CancellationToken ct)
    {
        var key = creationKey.ToString("D");
        var candidate = new ManualSnapshotSource(ObjectId.GenerateNewId().ToString(), tenant, key, url, clock.GetUtcNow().UtcDateTime);
        async Task<ManualSnapshotSource> UpsertAsync() => await sources.FindOneAndUpdateAsync(ManualSnapshotSourceFilters.Creation(tenant, key),
            Builders<ManualSnapshotSource>.Update.SetOnInsert(item => item.Id, candidate.Id).SetOnInsert(item => item.TenantId, tenant)
                .SetOnInsert(item => item.CreationKey, key).SetOnInsert(item => item.SourceUrl, url).SetOnInsert(item => item.CreatedAtUtc, candidate.CreatedAtUtc),
            new FindOneAndUpdateOptions<ManualSnapshotSource> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
        ManualSnapshotSource source;
        try { source = await UpsertAsync(); }
        catch (MongoWriteException ex) when (MongoDuplicateKey.IsForIndex(ex, ManualSnapshotSourceFilters.CreationKeyIndex))
        { source = await sources.Find(ManualSnapshotSourceFilters.Creation(tenant, key)).SingleAsync(ct); }
        catch (MongoCommandException ex) when (MongoDuplicateKey.IsForIndex(ex, ManualSnapshotSourceFilters.CreationKeyIndex))
        { source = await sources.Find(ManualSnapshotSourceFilters.Creation(tenant, key)).SingleAsync(ct); }
        return source.SourceUrl == url ? source : new SnapshotSourceRelocationRequired();
    }
    public async Task<ManualSnapshotSource?> LoadAsync(string tenant, string id, CancellationToken ct) =>
        await sources.Find(ManualSnapshotSourceFilters.Identity(tenant, ObjectId.Parse(id).ToString())).FirstOrDefaultAsync(ct);
    public async Task<GitSourceExecution?> ExecutionAsync(string tenant, string sourceId, CancellationToken ct) =>
        await executions.Find(GitSourceExecutionFilters.Source(tenant, sourceId)).FirstOrDefaultAsync(ct);
    public async Task<SnapshotSourceStatus> StatusAsync(ManualSnapshotSource source, CancellationToken ct)
    {
        var execution = await ExecutionAsync(source.TenantId, source.Id, ct);
        var owned = await inventory.Find(ManagedDeclarationFilters.SourceInventory(source.TenantId, source.Id)).ToListAsync(ct);
        var outcomes = execution is null ? [] : await receipts.Find(GitOperationReceiptFilters.ForRun(source.TenantId, source.Id, execution.RunId, execution.Fence))
            .SortBy(item => item.Kind).ThenBy(item => item.Name).ThenBy(item => item.Phase).Limit(SnapshotSchema.MaximumOperationReceipts).ToListAsync(ct);
        return new(source.Id, source.SourceUrl, execution?.Fence ?? 0, execution?.RunId, execution?.Status ?? "AwaitingSnapshot",
            execution?.ObservedCommit, execution?.AppliedCommit, execution?.NextAttemptAtUtc,
            owned.OrderBy(item => item.Kind).ThenBy(item => item.Name).Select(item => new SnapshotResourceStatus(item.Kind, item.Name, item.ResourceId,
                item.Blocker, item.PendingRemoval, item.ToDomain().ConfigurationApplied(item.Blocker is not null))).ToArray(),
            outcomes.Select(item => new SnapshotOperationStatus(item.Kind, item.Name, item.Phase, item.Blocker, item.Changed)).ToArray());
    }
    public Task WakeSecretAsync(string tenant, string name, CancellationToken ct) => executions.UpdateManyAsync(
        GitSourceExecutionFilters.SecretWaiters(tenant, name), Builders<GitSourceExecution>.Update.Set(item => item.NextAttemptAtUtc, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);
    public async Task<IReadOnlyList<GitSourceExecution>> DueAsync(string? tenant, CancellationToken ct)
    {
        var filter = GitSourceExecutionFilters.DueForTenant(tenant, clock.GetUtcNow().UtcDateTime);
        return await executions.Find(filter).SortBy(item => item.NextAttemptAtUtc).ThenBy(item => item.Id).Limit(SnapshotExecutionPolicy.RecoveryBatchSize).ToListAsync(ct);
    }
}
