using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application.Mongo;

internal static class GitRepositoryFilters
{
    public static FilterDefinition<RepositorySource> Tenant(string tenant) =>
        Builders<RepositorySource>.Filter.Eq(item => item.TenantId, tenant);

    public static FilterDefinition<RepositorySource> VisibleTenant(string tenant) => Tenant(tenant) &
        Builders<RepositorySource>.Filter.Ne(item => item.Activity, GitSourceActivity.Deleted);

    public static FilterDefinition<RepositorySource> VisibleSource(string tenant, string source) =>
        Source(tenant, source) & Builders<RepositorySource>.Filter.Ne(item => item.Activity, GitSourceActivity.Deleted);

    public static FilterDefinition<GitSynchronizationState> States(string tenant, IReadOnlyCollection<string> ids) =>
        Builders<GitSynchronizationState>.Filter.Eq(item => item.Source.TenantId, tenant) &
        Builders<GitSynchronizationState>.Filter.In(item => item.Source.SourceId, ids);

    public static FilterDefinition<GitReconciliationRun> Runs(string tenant, IReadOnlyCollection<string> ids) =>
        Builders<GitReconciliationRun>.Filter.Eq(item => item.Stamp.Source.TenantId, tenant) &
        Builders<GitReconciliationRun>.Filter.In(item => item.Id, ids);

    public static FilterDefinition<GitReconciliationRun> SourceRuns(string tenant, string source) =>
        Builders<GitReconciliationRun>.Filter.Eq(item => item.Stamp.Source.TenantId, tenant) &
        Builders<GitReconciliationRun>.Filter.Eq(item => item.Stamp.Source.SourceId, source);

    public static FilterDefinition<ManagedDeclaration> Inventory(string tenant, IReadOnlyCollection<string> ids) =>
        Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<ManagedDeclaration>.Filter.In(item => item.SourceId, ids);

    public static FilterDefinition<GitOperationReceipt> Receipts(
        string tenant,
        string source,
        string run) => Builders<GitOperationReceipt>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<GitOperationReceipt>.Filter.Eq(item => item.SourceId, source) &
        Builders<GitOperationReceipt>.Filter.Eq(item => item.RunId, run);

    public static FilterDefinition<GitSourceRequestReceipt> Request(string key) =>
        Builders<GitSourceRequestReceipt>.Filter.Eq(item => item.Id, key);

    public static string StateId(string tenant, string source) => $"{tenant}/{source}";

    public static FilterDefinition<RepositorySource> Source(string tenant, string source) =>
        Builders<RepositorySource>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<RepositorySource>.Filter.Eq(item => item.Id, source);

    public static FilterDefinition<RepositorySource> Creation(string tenant, string key) =>
        Builders<RepositorySource>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<RepositorySource>.Filter.Eq(item => item.CreationKey, key);

    public static FilterDefinition<GitSynchronizationState> State(string tenant, string source) =>
        Builders<GitSynchronizationState>.Filter.Eq(item => item.Id, StateId(tenant, source));

    public static FilterDefinition<GitReconciliationRun> Run(
        string tenant,
        string source,
        string run) => Builders<GitReconciliationRun>.Filter.Eq(item => item.Stamp.Source.TenantId, tenant) &
        Builders<GitReconciliationRun>.Filter.Eq(item => item.Stamp.Source.SourceId, source) &
        Builders<GitReconciliationRun>.Filter.Eq(item => item.Id, run);

    public static FilterDefinition<GitReconciliationRun> PreviousRuns(GitRunStamp stamp) =>
        SourceRuns(stamp.Source.TenantId, stamp.Source.SourceId) &
        Builders<GitReconciliationRun>.Filter.Lt(item => item.Stamp.Generation, stamp.Generation);

    public static FilterDefinition<GitReconciliationRun> LifecycleInput() =>
        Builders<GitReconciliationRun>.Filter.OfType<GitRunInput, GitInputLifecycle>(item => item.Input);

    public static FilterDefinition<GitReconciliationRun> HumanConfirmedLifecycle() =>
        Builders<GitReconciliationRun>.Filter.OfType<GitRunInput, GitInputLifecycle>(item => item.Input) &
        Builders<GitReconciliationRun>.Filter.OfType<GitPreviewState, GitPreviewConfirmed>(item =>
            ((GitInputLifecycle)item.Input).Plan.Authorization);

    public static FilterDefinition<GitReconciliationRun> PriorHumanLifecycle(GitRunStamp stamp) =>
        PreviousRuns(stamp) & HumanConfirmedLifecycle();

    public static FilterDefinition<RepositorySource> SourceRevision(GitRunStamp stamp) =>
        Source(stamp.Source.TenantId, stamp.Source.SourceId) & Builders<RepositorySource>.Filter.Eq(
            item => item.Revision,
            stamp.ConfigurationRevision);

    public static FilterDefinition<GitSynchronizationState> ActiveState(GitRunStamp stamp) =>
        State(stamp.Source.TenantId, stamp.Source.SourceId) &
        Builders<GitSynchronizationState>.Filter.Eq(item => item.Fence, stamp.Fence) &
        Builders<GitSynchronizationState>.Filter.Eq(item => item.CurrentRun!.RunId, stamp.RunId) &
        Builders<GitSynchronizationState>.Filter.Eq(item => item.CurrentRun!.Generation, stamp.Generation);

    public static FilterDefinition<GitReconciliationRun> LiveClaim(GitRunStamp stamp, GitClaimIdentity claim) =>
        Run(stamp.Source.TenantId, stamp.Source.SourceId, stamp.RunId) &
        Builders<GitReconciliationRun>.Filter.Eq(item => item.Stamp.Fence, stamp.Fence) &
        Builders<GitReconciliationRun>.Filter.Eq(
            item => item.Stamp.ConfigurationRevision,
            stamp.ConfigurationRevision) &
        Builders<GitReconciliationRun>.Filter.OfType<GitRunState, GitRunClaimed>(item => item.State) &
        Builders<GitReconciliationRun>.Filter.Eq(item => ((GitRunClaimed)item.State).Lease.WorkerId, claim.WorkerId) &
        Builders<GitReconciliationRun>.Filter.Eq(item => ((GitRunClaimed)item.State).Lease.Attempt, claim.Attempt) &
        Builders<GitReconciliationRun>.Filter.AfterServerNow(item => ((GitRunClaimed)item.State).Lease.ExpiresAtUtc);
}

internal static class ManagedDeclarationFilters
{
    public static string AddressId(string tenant, ResourceAddress address) => $"{tenant}/{address.Kind}/{address.Name}";

    public static FilterDefinition<ManagedDeclaration> Address(string tenant, ResourceAddress address) =>
        Builders<ManagedDeclaration>.Filter.Eq(item => item.Id, AddressId(tenant, address));

    public static FilterDefinition<ManagedDeclaration> Resource(
        string tenant,
        string kind,
        string id) => Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<ManagedDeclaration>.Filter.Eq(item => item.Kind, kind) &
        Builders<ManagedDeclaration>.Filter.Eq(item => item.ResourceId, id);

    public static FilterDefinition<ManagedDeclaration> Resources(
        string tenant,
        string kind,
        IReadOnlyCollection<string> ids) => Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<ManagedDeclaration>.Filter.Eq(item => item.Kind, kind) &
        Builders<ManagedDeclaration>.Filter.In(item => item.ResourceId, ids);

    public static FilterDefinition<ManagedDeclaration> SourceInventory(string tenant, string source) =>
        Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<ManagedDeclaration>.Filter.Eq(item => item.SourceId, source);

    public static FilterDefinition<ManagedDeclaration> CreatedResources(
        string tenant,
        string source,
        IReadOnlyCollection<string> ids) => SourceInventory(tenant, source) &
        Builders<ManagedDeclaration>.Filter.Eq(item => item.Materialization, GitResourceMaterialization.Created) &
        Builders<ManagedDeclaration>.Filter.In(item => item.ResourceId, ids);

    public static FilterDefinition<ManagedDeclaration> PendingRemoval(string tenant, string source) =>
        SourceInventory(tenant, source) & Builders<ManagedDeclaration>.Filter.Eq(item => item.PendingRemoval, true);
}

internal static class GitLifecycleCleanupFilters
{
    public static FilterDefinition<GitOperationReceipt> BeginReceipts(
        string tenant,
        string source,
        string kind,
        string resourceId) => Builders<GitOperationReceipt>.Filter.Where(item => item.TenantId == tenant &&
        item.SourceId == source && item.Kind == kind && item.ResourceId == resourceId && item.Blocker == null &&
        item.Lifecycle != null && item.Lifecycle.Step == GitLifecycleStep.BeginRemoval);

    public static FilterDefinition<GitReconciliationRun> Run(
        string tenant,
        string source,
        string run) => Builders<GitReconciliationRun>.Filter.Where(item =>
        item.Id == run && item.Stamp.Source.TenantId == tenant && item.Stamp.Source.SourceId == source);

    public static FilterDefinition<GitLifecycleCleanupProof> Proof(string id) =>
        Builders<GitLifecycleCleanupProof>.Filter.Eq(item => item.Id, id);

    public static FilterDefinition<GitLifecycleCleanupProof> Proofs(GitRunStamp stamp, string runId) =>
        Builders<GitLifecycleCleanupProof>.Filter.Where(item => item.TenantId == stamp.Source.TenantId &&
            item.SourceId == stamp.Source.SourceId && item.RunId == runId);
}

internal static class GitLifecyclePreviewFilters
{
    public static FilterDefinition<GitOperationPreview> Source(GitSourceIdentity source) =>
        Builders<GitOperationPreview>.Filter.Eq(item => item.Source.Source.TenantId, source.TenantId) &
        Builders<GitOperationPreview>.Filter.Eq(item => item.Source.Source.SourceId, source.SourceId);

    public static FilterDefinition<GitOperationPreview> Preview(GitSourceIdentity source, string preview) =>
        Source(source) & Builders<GitOperationPreview>.Filter.Eq(item => item.Id, preview);

    public static FilterDefinition<GitOperationPreview> Revision(
        GitSourceIdentity source,
        string preview,
        long revision) => Preview(source, preview) &
        Builders<GitOperationPreview>.Filter.Eq(item => item.Revision, revision);

    public static FilterDefinition<GitOperationPreview> TenantPreviews(string tenant, IEnumerable<string> previews) =>
        Builders<GitOperationPreview>.Filter.Eq(item => item.Source.Source.TenantId, tenant) &
        Builders<GitOperationPreview>.Filter.In(item => item.Id, previews);
}

internal static class GitAcceptedReferenceFilters
{
    public static string Key(
        GitSourceIdentity source,
        string consumer,
        string kind,
        string name) => $"{source.TenantId}/{source.SourceId}/{consumer}/{kind}/{name}";

    public static FilterDefinition<GitAcceptedReference> Identity(string id) =>
        Builders<GitAcceptedReference>.Filter.Eq(item => item.Id, id);

    public static FilterDefinition<GitAcceptedReference> Target(
        string tenant,
        string kind,
        string resource) => Builders<GitAcceptedReference>.Filter.Eq(item => item.Source.TenantId, tenant) &
        Builders<GitAcceptedReference>.Filter.Eq(item => item.Target.Kind, kind) &
        Builders<GitAcceptedReference>.Filter.Eq(item => item.Target.ResourceId, resource);

    public static FilterDefinition<GitAcceptedReference> Consumer(GitSourceIdentity source, string consumer) =>
        Builders<GitAcceptedReference>.Filter.Eq(item => item.Source.TenantId, source.TenantId) &
        Builders<GitAcceptedReference>.Filter.Eq(item => item.Source.SourceId, source.SourceId) &
        Builders<GitAcceptedReference>.Filter.Eq(item => item.ConsumerResourceId, consumer);
}

internal static class GitOperationPlanFilters
{
    public static FilterDefinition<GitPlannedOperation> Runs(string tenant, IReadOnlyCollection<string> runs) =>
        Builders<GitPlannedOperation>.Filter.Eq(item => item.Stamp.Run.Source.TenantId, tenant) &
        Builders<GitPlannedOperation>.Filter.In(item => item.Stamp.Run.RunId, runs);

    public static FilterDefinition<GitPlannedOperation> Identity(string id) =>
        Builders<GitPlannedOperation>.Filter.Eq(item => item.Id, id);

    public static FilterDefinition<GitPlannedOperation> Run(
        string tenant,
        string source,
        string run) => Builders<GitPlannedOperation>.Filter.Eq(item => item.Stamp.Run.Source.TenantId, tenant) &
        Builders<GitPlannedOperation>.Filter.Eq(item => item.Stamp.Run.Source.SourceId, source) &
        Builders<GitPlannedOperation>.Filter.Eq(item => item.Stamp.Run.RunId, run);
}

internal static class GitOperationReceiptFilters
{
    public static FilterDefinition<GitOperationReceipt> Runs(string tenant, IReadOnlyCollection<string> runs) =>
        Builders<GitOperationReceipt>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<GitOperationReceipt>.Filter.In(item => item.RunId, runs);

    public static FilterDefinition<GitOperationReceipt> ForRun(
        string tenant,
        string source,
        string run,
        long fence) => Builders<GitOperationReceipt>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<GitOperationReceipt>.Filter.Eq(item => item.SourceId, source) &
        Builders<GitOperationReceipt>.Filter.Eq(item => item.RunId, run) &
        Builders<GitOperationReceipt>.Filter.Eq(item => item.Fence, fence);

    public static FilterDefinition<GitOperationReceipt> Identity(string id) => Builders<GitOperationReceipt>.Filter.Eq(
        item => item.Id,
        id);
}

internal static class GitSchedulingFilters
{
    public static FilterDefinition<RepositorySource> EligibleSources(GitSchedulingEligibility eligibility) =>
        eligibility == GitSchedulingEligibility.Active
            ? Builders<RepositorySource>.Filter.Eq(item => item.Activity, GitSourceActivity.Active)
            : Builders<RepositorySource>.Filter.In(
                item => item.Activity,
                [GitSourceActivity.Active, GitSourceActivity.Paused, GitSourceActivity.Detaching]);

    public static FilterDefinition<RepositorySource> Repository(RepositorySyncSignal signal) =>
        Builders<RepositorySource>.Filter.Eq(item => item.Activity, GitSourceActivity.Active) &
        Builders<RepositorySource>.Filter.Eq(item => item.TenantId, signal.TenantId) &
        Builders<RepositorySource>.Filter.Eq(item => item.Repository.Provider, signal.Provider) &
        Builders<RepositorySource>.Filter.Eq(item => item.Repository.ConnectionId, signal.ConnectionId) &
        Builders<RepositorySource>.Filter.Eq(
            item => item.Repository.ExternalRepositoryId,
            signal.ExternalRepositoryId) &
        Builders<RepositorySource>.Filter.Eq(item => item.Repository.Branch, signal.Branch);

    public static FilterDefinition<GitSynchronizationState> Pending() =>
        Builders<GitSynchronizationState>.Filter.Eq(item => item.CurrentRun, null) &
        Builders<GitSynchronizationState>.Filter.Where(item => item.RequestedGeneration > item.CompletedGeneration);

    public static FilterDefinition<GitSynchronizationState> PollDue(DateTime now) =>
        Builders<GitSynchronizationState>.Filter.Lte(item => item.NextPollAtUtc, now);

    public static FilterDefinition<GitReconciliationRun> Recoverable() =>
        Builders<GitReconciliationRun>.Filter.OfType<GitRunState, GitRunWaiting>(item => item.State) |
        (Builders<GitReconciliationRun>.Filter.OfType<GitRunState, GitRunDelayed>(item => item.State) &
            Builders<GitReconciliationRun>.Filter.AtOrBeforeServerNow(item =>
                ((GitRunDelayed)item.State).NextAttemptAtUtc)) |
        (Builders<GitReconciliationRun>.Filter.OfType<GitRunState, GitRunWaitingCleanup>(item => item.State) &
            Builders<GitReconciliationRun>.Filter.AtOrBeforeServerNow(item =>
                ((GitRunWaitingCleanup)item.State).NextAttemptAtUtc)) |
        (Builders<GitReconciliationRun>.Filter.OfType<GitRunState, GitRunClaimed>(item => item.State) &
            Builders<GitReconciliationRun>.Filter.AtOrBeforeServerNow(item =>
                ((GitRunClaimed)item.State).Lease.ExpiresAtUtc));
}
