namespace Pole.Features.Postgres.Application.Backups

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open k8s.Autorest
open Pole.Features.Backups.Contracts
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Application.Clusters
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Kubernetes
open Pole.Features.Postgres.Domain

module private ProviderResult =
    let protect (work: unit -> Task<'t>) (ct: CancellationToken) =
        task {
            try
                let! value = work()
                return BackupProviderResult<'t>.Success(value) :> BackupProviderResult<'t>
            with
            | PostgresBackupFailure(code,retry) -> return BackupProviderResult<'t>.Failure(code,retry) :> _
            | :? DeploymentTargetUnavailableException -> return BackupProviderResult<'t>.Failure("backup-target-unavailable",true) :> _
            | :? HttpOperationException as e ->
                let denied = e.Response.StatusCode = HttpStatusCode.Forbidden || e.Response.StatusCode = HttpStatusCode.Unauthorized
                let code = if denied then "backup-kubernetes-access-denied" else "backup-kubernetes-unavailable"
                return BackupProviderResult<'t>.Failure(code,true) :> _
            | :? HttpRequestException -> return BackupProviderResult<'t>.Failure("backup-target-unavailable",true) :> _
            | :? OperationCanceledException when not ct.IsCancellationRequested ->
                return BackupProviderResult<'t>.Failure("backup-target-timeout",true) :> _
        }

type PostgresBackupProvider
    (clients: IKubernetesClientProvider, load: LoadPostgresClusterById, recovery: PostgresBackupRecovery) =

    let resourceType = PostgresResourceDeployment.ResourceType
    let clientFor (archive: BackupArchiveContext) ct = clients.GetAsync(archive.Placement.DeploymentTargetId, ct)

    interface IBackupProvider with
        member _.ResourceType = resourceType.Value
        member _.DeclarationKind = "PostgresCluster"
        member _.SupportsPointInTimeRecovery = true
        member _.ValidateSchedule(schedule) =
            if schedule.RetentionDays > 0 then ValidateBackupScheduleOutcome(BackupScheduleAccepted())
            else ValidateBackupScheduleOutcome(BackupRetentionInvalid())

        member _.DescribeAsync(source, ct) =
            task {
                if source.ResourceType <> resourceType.Value then
                    return DescribeBackupSourceOutcome(BackupSourceUnsupported())
                else
                    let! current = load.Invoke(source.TenantId, source.ResourceId, ct)
                    match current with
                    | Some cluster when cluster.Lifecycle :? ActivePostgresClusterLifecycle && not cluster.IsPrepared ->
                        return DescribeBackupSourceOutcome(
                            BackupServiceSnapshot(string cluster.Spec.Version, cluster.Spec.Storage,
                                cluster.Spec.Instances, cluster.Revision))
                    | Some _
                    | None -> return DescribeBackupSourceOutcome(BackupSourceUnavailable())
            }

        member _.ListAsync(request,ct) = BarmanCatalog.read request ct

        member _.ApplyScheduleAsync(request, ct) =
            task {
                let! client =
                    match request.Configuration with
                    | :? StopBackupPolicy -> clients.GetForCleanupAsync(request.Archive.Placement.DeploymentTargetId, ct)
                    | :? ConfigureBackupPolicy -> clientFor request.Archive ct
                    | _ -> invalidOp "Unsupported backup policy configuration."
                try
                    let! result = PostgresBackupPolicy.apply client request ct
                    return PostgresBackupPolicy.outcome result
                with
                | :? HttpOperationException as error when error.Response.StatusCode = HttpStatusCode.Conflict ->
                    // The observed UID/resourceVersion or a missing-object create lost a race.
                    return ApplyBackupScheduleOutcome(BackupNativeWriteConflict())
            }

        member _.ObserveAsync(archive,ct) =
            ProviderResult.protect (fun () -> task {
                let! client = clientFor archive ct
                return! BackupNative.observe client archive ct }) ct

        member _.RunAsync(request, ct) =
            task {
                let! client = clientFor request.Archive ct
                return! PostgresBackupCopy.run client request ct
            }

        member _.ObserveCopyAsync(request, ct) =
            task {
                let! client = clientFor request.Archive ct
                return! PostgresBackupCopy.observe client request ct
            }

        member _.RestoreAsync(request, ct) =
            task {
                let! client = clientFor request.Archive ct
                return! recovery.CreateAsync(client, request, ct)
            }

        member _.ObserveRestoreAsync(request, ct) =
            task {
                let! client = clientFor request.Archive ct
                return! recovery.ObserveAsync(client, request, ct)
            }
