using Microsoft.Extensions.DependencyInjection;
using Pole.Database;
using Pole.Features.Backups.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Observability;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.Backups.Tests.Integration;

internal sealed class TestBackupProvider(
    IServiceScopeFactory scopes
) : IBackupProvider
{
    public string ResourceType => "pole/test-backup-database";
    public string DeclarationKind => "TestDatabase";
    public bool SupportsPointInTimeRecovery => true;

    public ValidateBackupScheduleOutcome ValidateSchedule(BackupSchedule schedule) => schedule.RetentionDays > 0
        ? new BackupScheduleAccepted()
        : new BackupRetentionInvalid();

    public IReadOnlyList<BackupCatalogEntry> Catalog { get; set; } = [];
    public BackupServiceSnapshot Service { get; set; } = new("17", 4, 1, 1);
    public int? RestoreStorageGiB { get; private set; }
    public Func<RestoreBackupRequest, CancellationToken, Task<RestoreBackupOutcome>>? CreateTarget { get; set; }
    public ReadBackupCatalogOutcome? CatalogResponse { get; set; }
    private int _catalogCount;
    public int CatalogCount => Volatile.Read(ref _catalogCount);
    private int _applyCount;
    private int _runCount;
    public int ApplyCount => Volatile.Read(ref _applyCount);
    public int RunCount => Volatile.Read(ref _runCount);
    public ObserveBackupCopyOutcome CopyObservation { get; set; } = new BackupCopyMissing();
    public TraceContextSnapshot? LastApplyTrace { get; private set; }
    public TraceContextSnapshot? LastRunTrace { get; private set; }

    public Task<DescribeBackupSourceOutcome> DescribeAsync(BackupSourceIdentity source, CancellationToken ct) =>
        Task.FromResult<DescribeBackupSourceOutcome>(Service);

    public Task<ReadBackupCatalogOutcome> ListAsync(ListBackupCatalogRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _catalogCount);
        return Task.FromResult(CatalogResponse ?? new BackupCatalogContents(Catalog));
    }

    public IReadOnlyList<NativeScheduledBackupChange> ObservedRuns { get; set; } =
    [
        new(
            "native-id",
            new NativeScheduledBackupCompleted("before"),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow),
        new("running-uid", new NativeScheduledBackupRunning(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
    ];

    public bool NativeScheduleEnabled { get; set; } = true;
    public Func<BackupArchiveContext, ObserveBackupRuntimeOutcome>? ObserveRuntime { get; set; }

    public Task<ObserveBackupRuntimeOutcome> ObserveAsync(BackupArchiveContext archive, CancellationToken ct) =>
        Task.FromResult<ObserveBackupRuntimeOutcome>(
            ObserveRuntime?.Invoke(archive) ?? new ObserveBackupRuntimeOutcome(
                new BackupRuntimeObservation("Healthy", null, DateTimeOffset.UtcNow, null, NativeScheduleEnabled)));

    public Func<ReadBackupHistoryRequest, CancellationToken, Task<ReadBackupHistoryOutcome>>? ReadHistory { get; set; }
    public ReadBackupHistoryRequest? LastHistoryRequest { get; private set; }
    public TraceContextSnapshot? LastHistoryTrace { get; private set; }

    public Task<ReadBackupHistoryOutcome> ReadHistoryAsync(ReadBackupHistoryRequest request, CancellationToken ct)
    {
        LastHistoryRequest = request;
        LastHistoryTrace = TraceContext.Current;
        return ReadHistory is { } read
            ? read(request, ct)
            : Task.FromResult<ReadBackupHistoryOutcome>(
                new BackupHistoryBatch(
                    new FollowNativeBackupHistory(new("publisher", "opaque")),
                    1,
                    ObservedRuns.Select(run => new NativeBackupActivity(
                            run.NativeUid,
                            run.State is NativeScheduledBackupRunning))
                        .ToArray(),
                    ObservedRuns,
                    ObservedRuns.Count));
    }

    public Task<SuspendBackupArchiveOutcome> SuspendArchiveAsync(
        SuspendBackupArchiveRequest request,
        CancellationToken ct) => Task.FromResult<SuspendBackupArchiveOutcome>(
        new BackupArchiveSuspended(new(request.PolicyId, request.PolicyRevision, "publisher")));

    public Func<ApplyBackupScheduleRequest, ApplyBackupScheduleOutcome>? Apply { get; set; }

    public Task<ApplyBackupScheduleOutcome> ApplyScheduleAsync(ApplyBackupScheduleRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _applyCount);
        LastApplyTrace = TraceContext.Current;
        return Task.FromResult(Apply?.Invoke(request) ?? new ApplyBackupScheduleOutcome(new BackupPolicyApplied()));
    }

    public Task<ObserveBackupCopyOutcome> ObserveCopyAsync(ObserveBackupCopyRequest request, CancellationToken ct) =>
        Task.FromResult(CopyObservation);

    public Task<RunBackupCopyOutcome> RunAsync(BackupCopyRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _runCount);
        LastRunTrace = TraceContext.Current;
        return Task.FromResult<RunBackupCopyOutcome>(new BackupCopyCompleted(request.OperationId));
    }

    public async Task<RestoreBackupOutcome> RestoreAsync(RestoreBackupRequest request, CancellationToken ct)
    {
        RestoreStorageGiB = request.Resource.StorageGiB;
        return CreateTarget is null ? await RecordDefaultTargetAsync(request, ct) : await CreateTarget(request, ct);
    }

    public ObserveBackupRestoreOutcome? RestoreObservation { get; set; }

    public async Task<ObserveBackupRestoreOutcome> ObserveRestoreAsync(
        ObserveBackupRestoreRequest request,
        CancellationToken ct)
    {
        if (RestoreObservation is { } observed)
            return observed;
        await using var scope = scopes.CreateAsyncScope();
        var resource = await scope.ServiceProvider.GetRequiredService<LoadRegisteredResource>()(
            new(
                request.Archive.Source.TenantId,
                ResourceTypeId.Create(request.Archive.Source.ResourceType),
                request.ResourceId),
            ct);
        return resource is null ? new BackupRestoreTargetMissing() : new BackupRestoreCompleted(resource.ResourceId);
    }

    private async Task<RestoreBackupOutcome> RecordDefaultTargetAsync(
        RestoreBackupRequest request,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var receipts = services.GetRequiredService<RecordRestoreTargetCreation>();
        var registrar = services.GetRequiredService<IResourceRegistrar>();
        var transactions = services.GetRequiredService<MongoRunTransaction>();
        await transactions.InvokeOutcomeAsync<bool>(
            async (transaction, token) =>
            {
                var archive = request.Archive;
                var recorded = await receipts(
                    new(archive.Source.TenantId, request.OperationId, archive.Location.ArchiveId, request.Resource.Id),
                    transaction.Session,
                    token);
                Assert.Equal(RestoreTargetCreationDecision.Recorded, recorded);
                var registered = await registrar.RegisterAsync(
                    new(
                        archive.Source.TenantId,
                        archive.Placement.ResourceGroupId,
                        new(
                            ResourceTypeId.Create(archive.Source.ResourceType),
                            request.Resource.Id,
                            request.Resource.Name)),
                    transaction.Session,
                    token);
                Assert.IsType<RegisteredResource>(registered.Value);
                return transaction.Commit(true);
            },
            ct);
        return new BackupRestoreCompleted(request.Resource.Id);
    }
}
