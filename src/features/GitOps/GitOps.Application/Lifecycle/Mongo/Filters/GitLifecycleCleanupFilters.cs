using MongoDB.Driver;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Lifecycle.Mongo.Filters;

internal static class GitLifecycleCleanupFilters
{
    public static FilterDefinition<GitOperationReceipt> BeginReceipts(string tenant, string source, string kind,
        string resourceId) => Builders<GitOperationReceipt>.Filter.Where(item => item.TenantId == tenant &&
        item.SourceId == source && item.Kind == kind && item.ResourceId == resourceId && item.Blocker == null &&
        item.Lifecycle != null && item.Lifecycle.Step == GitLifecycleStep.BeginRemoval);

    public static FilterDefinition<GitReconciliationRun> Run(string tenant, string source, string run) =>
        Builders<GitReconciliationRun>.Filter.Where(item => item.Id == run && item.Stamp.Source.TenantId == tenant &&
            item.Stamp.Source.SourceId == source);

    public static FilterDefinition<GitLifecycleCleanupProof> Proof(string id) =>
        Builders<GitLifecycleCleanupProof>.Filter.Eq(item => item.Id, id);

    public static FilterDefinition<GitLifecycleCleanupProof> Proofs(GitRunStamp stamp, string runId) =>
        Builders<GitLifecycleCleanupProof>.Filter.Where(item => item.TenantId == stamp.Source.TenantId &&
            item.SourceId == stamp.Source.SourceId && item.RunId == runId);
}
