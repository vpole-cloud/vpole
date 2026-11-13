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
    public Func<RestoreBackupRequest, CancellationToken, Task<BackupProviderPhase>>? CreateTarget { get; set; }
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

    public IReadOnlyList<BackupObservedRun> ObservedRuns { get; set; } =
    [
        new(
            "native-id",
            "before",
            BackupObservedRunState.Succeeded,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            null),
        new("running-uid", null, BackupObservedRunState.Running, DateTimeOffset.UtcNow, null, null),
    ];

    public Task<BackupProviderResult<BackupRuntimeObservation>> ObserveAsync(
        BackupArchiveContext archive,
        CancellationToken ct) => Success(
        new BackupRuntimeObservation("Healthy", null, DateTimeOffset.UtcNow, null, ObservedRuns));

    public Task<ApplyBackupScheduleOutcome> ApplyScheduleAsync(ApplyBackupScheduleRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _applyCount);
        LastApplyTrace = TraceContext.Current;
        return Task.FromResult<ApplyBackupScheduleOutcome>(new BackupPolicyApplied());
    }

    public Task<ObserveBackupCopyOutcome> ObserveCopyAsync(ObserveBackupCopyRequest request, CancellationToken ct) =>
        Task.FromResult(CopyObservation);

    public Task<RunBackupCopyOutcome> RunAsync(BackupCopyRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _runCount);
        LastRunTrace = TraceContext.Current;
        return Task.FromResult<RunBackupCopyOutcome>(new BackupCopyCompleted(request.OperationId));
    }

    public async Task<BackupProviderResult<BackupProviderProgress>> RestoreAsync(
        RestoreBackupRequest request,
        CancellationToken ct)
    {
        RestoreStorageGiB = request.Resource.StorageGiB;
        var phase = CreateTarget is null
            ? await RecordDefaultTargetAsync(request, ct)
            : await CreateTarget(request, ct);
        return new BackupProviderResult<BackupProviderProgress>.Success(new(phase, null));
    }

    private async Task<BackupProviderPhase> RecordDefaultTargetAsync(RestoreBackupRequest request, CancellationToken ct)
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
        return BackupProviderPhase.Succeeded;
    }

    private static Task<BackupProviderResult<T>> Success<T>(T result) =>
        Task.FromResult<BackupProviderResult<T>>(new BackupProviderResult<T>.Success(result));
}
