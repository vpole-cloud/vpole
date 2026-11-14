namespace Pole.Features.Postgres.Application.Backups

open System
open System.Net
open System.Net.Http
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open k8s
open k8s.Autorest
open k8s.Models
open Pole.Features.Backups.Contracts
open Pole.Features.Core.Kubernetes
open Pole.Features.Postgres.Application.Kubernetes

module internal PostgresBackupHistory =
    open PostgresBackupObjects
    open PostgresBackupArchive
    module M = PostgresBackupManifests
    module Status = PostgresBackupStatus

    let private maximum = 200

    type private Batch =
        { Activities: ResizeArray<NativeBackupActivity>
          Changes: ResizeArray<NativeScheduledBackupChange>
          mutable Consumed: int
          mutable Version: string
          mutable Unreadable: bool }

    let private belongsTo kind uid native =
        array native ["metadata"; "ownerReferences"]
        |> Seq.exists (fun owner -> value owner ["kind"] = kind && value owner ["uid"] = uid)

    let private accept (request: ReadBackupHistoryRequest) (sourceIdentity: Identity) publisher eventType native (batch: Batch) =
        batch.Consumed <- batch.Consumed + 1
        let version = value native ["metadata"; "resourceVersion"]
        if String.IsNullOrWhiteSpace version then batch.Unreadable <- true
        else batch.Version <- version
        if eventType <> WatchEventType.Bookmark then
            let scheduled = belongsTo "ScheduledBackup" publisher native
            let manual = ownsArchive native request.Archive && belongsTo "Cluster" sourceIdentity.Uid native
            if (scheduled || manual) && value native ["spec"; "cluster"; "name"] = M.clusterName request.Archive then
                match identity native, Status.read native with
                | None, _ | _, Error _ -> batch.Unreadable <- true
                | Some identity, Ok state ->
                    let deleted = eventType = WatchEventType.Deleted
                    let running = match state with Status.Running when not deleted -> true | _ -> false
                    batch.Activities.Add(NativeBackupActivity(identity.Uid, running))
                    if scheduled then
                        let created = timestamp native ["metadata"; "creationTimestamp"]
                        if not created.HasValue then batch.Unreadable <- true
                        else
                            let state: NativeScheduledBackupState =
                                match state with
                                | Status.Running when deleted -> NativeScheduledBackupRemoved()
                                | Status.Running -> NativeScheduledBackupRunning()
                                | Status.Completed id -> NativeScheduledBackupCompleted(id)
                                | Status.Failed failure -> NativeScheduledBackupFailed(Status.code failure)
                            let finished = timestamp native ["status"; "stoppedAt"]
                            batch.Changes.Add(NativeScheduledBackupChange(
                                identity.Uid, state, created.Value, finished.GetValueOrDefault(created.Value)))

    let private batchOutcome revision position (batch: Batch) =
        if batch.Unreadable then ReadBackupHistoryOutcome(BackupHistoryUnreadable())
        elif String.IsNullOrWhiteSpace batch.Version then ReadBackupHistoryOutcome(BackupHistoryUnreadable())
        else ReadBackupHistoryOutcome(BackupHistoryBatch(position, revision, batch.Activities, batch.Changes, batch.Consumed))

    let private list (client: IKubernetes) (request: ReadBackupHistoryRequest) sourceIdentity publisher revision
                     (position: NativeBackupHistoryPosition) ct =
        task {
            let continuation, expected =
                match position with
                | :? BeginNativeBackupHistory -> null, None
                | :? PageNativeBackupHistory as page -> page.Continuation, Some page.Stream
                | _ -> invalidOp "A LIST requires an initial or paged position."
            let! result = client.CustomObjects.ListNamespacedCustomObjectAsync(
                PostgresBackupNames.Group, "v1", request.Archive.Placement.NamespaceName, "backups",
                labelSelector = ("cnpg.io/cluster=" + M.clusterName request.Archive),
                limit = Nullable maximum, continueParameter = continuation, cancellationToken = ct)
            let doc = JsonSerializer.SerializeToNode(result)
            let version = value doc ["metadata"; "resourceVersion"]
            let stream = NativeBackupStream(publisher, version)
            let items = array doc ["items"] |> Seq.toArray
            if not (field doc ["items"] :? JsonArray) || items.Length > maximum || String.IsNullOrWhiteSpace version then
                return ReadBackupHistoryOutcome(BackupHistoryUnreadable())
            elif expected |> Option.exists (fun previous -> previous <> stream) then
                return ReadBackupHistoryOutcome(BackupHistoryRestartRequired())
            else
                let batch = { Activities = ResizeArray(); Changes = ResizeArray(); Consumed = 0;
                              Version = version; Unreadable = false }
                for native in items do accept request sourceIdentity publisher WatchEventType.Added native batch
                // Item versions are not collection checkpoints. A paged LIST retains its snapshot version.
                batch.Version <- version
                let next: NativeBackupHistoryPosition =
                    match value doc ["metadata"; "continue"] with
                    | token when not (String.IsNullOrEmpty token) -> PageNativeBackupHistory(stream, token)
                    | _ -> FollowNativeBackupHistory(stream)
                return batchOutcome revision next batch
        }

    let private expired (error: Exception) =
        match error with
        | :? HttpOperationException as error -> error.Response.StatusCode = HttpStatusCode.Gone
        | :? KubernetesException as error -> not (isNull error.Status) && error.Status.Code = Nullable 410
        | _ -> false

    let private watch (client: IKubernetes) (request: ReadBackupHistoryRequest) sourceIdentity publisher revision
                      (stream: NativeBackupStream) (ct: CancellationToken) =
        task {
            use watching = CancellationTokenSource.CreateLinkedTokenSource(ct)
            let failed = TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously)
            let onError (error: Exception) =
                if error :? OperationCanceledException && watching.IsCancellationRequested then ()
                elif failed.TrySetResult error then watching.Cancel()
            let batch = { Activities = ResizeArray(); Changes = ResizeArray(); Consumed = 0;
                          Version = stream.Version; Unreadable = false }
            use response = KubernetesWatchHeaders.Begin(Action(ignore), watching.Token)
            try
                use events = client.CustomObjects.WatchListNamespacedCustomObjectAsync(
                    PostgresBackupNames.Group, "v1", request.Archive.Placement.NamespaceName, "backups",
                    allowWatchBookmarks = Nullable true,
                    labelSelector = ("cnpg.io/cluster=" + M.clusterName request.Archive),
                    resourceVersion = stream.Version, timeoutSeconds = Nullable 15,
                    onError = Action<Exception>(onError), cancellationToken = watching.Token).GetAsyncEnumerator(watching.Token)
                let mutable more = true
                while more && batch.Consumed < maximum && not batch.Unreadable do
                    let! moved = events.MoveNextAsync().AsTask()
                    if not moved then more <- false
                    else
                        let struct(eventType, item) = events.Current
                        let native = JsonSerializer.SerializeToNode(item)
                        accept request sourceIdentity publisher eventType native batch
            with
            | :? OperationCanceledException when watching.IsCancellationRequested -> ()
            if failed.Task.IsCompletedSuccessfully then
                let error = failed.Task.Result
                if expired error then return ReadBackupHistoryOutcome(BackupHistoryRestartRequired())
                elif error :? JsonException then return ReadBackupHistoryOutcome(BackupHistoryUnreadable())
                elif error :? EndOfStreamException ||
                     (match error with :? HttpRequestException as http -> http.InnerException :? EndOfStreamException | _ -> false) then
                    return batchOutcome revision (FollowNativeBackupHistory(NativeBackupStream(publisher, batch.Version))) batch
                else return raise error
            else
                let next = FollowNativeBackupHistory(NativeBackupStream(publisher, batch.Version))
                return batchOutcome revision next batch
        }

    let private readReady client (request: ReadBackupHistoryRequest) sourceIdentity schedule ct =
        task {
            match identity schedule, PostgresBackupPolicy.observedRevision schedule with
            | None, _ -> return ReadBackupHistoryOutcome(BackupNativeIdentityUnavailable())
            | _, None -> return ReadBackupHistoryOutcome(BackupHistoryUnreadable())
            | Some publisher, Some revision ->
                let suspended = PostgresBackupPolicy.suspension request.PolicyId schedule
                let permitted =
                    match request.Mode with
                    | :? LiveNativeBackupHistory -> true
                    | :? SuspendedNativeBackupHistory as drain -> suspended = Some drain.Suspension
                    | _ -> invalidOp "Unsupported native history mode."
                if not permitted then return ReadBackupHistoryOutcome(BackupHistorySuspensionChanged())
                else
                    let previous =
                        match request.Position with
                        | :? BeginNativeBackupHistory -> None
                        | :? PageNativeBackupHistory as page -> Some page.Stream
                        | :? FollowNativeBackupHistory as follow -> Some follow.Stream
                        | _ -> invalidOp "Unsupported native history position."
                    if previous |> Option.exists (fun stream -> stream.PublisherId <> publisher.Uid) then
                        return ReadBackupHistoryOutcome(BackupHistoryRestartRequired())
                    else
                        match request.Position with
                        | :? FollowNativeBackupHistory as follow ->
                            return! watch client request sourceIdentity publisher.Uid revision follow.Stream ct
                        | :? BeginNativeBackupHistory | :? PageNativeBackupHistory ->
                            return! list client request sourceIdentity publisher.Uid revision request.Position ct
                        | _ -> return invalidOp "Unsupported native history position."
        }

    let read (client: IKubernetes) (request: ReadBackupHistoryRequest) (budget: TimeSpan) (ct: CancellationToken) =
        task {
            if budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(15.0) then
                invalidArg (nameof budget) "A history step must be bounded to fifteen seconds."
            use deadline = CancellationTokenSource.CreateLinkedTokenSource(ct)
            deadline.CancelAfter budget
            try
                let! loaded = source client request.Archive deadline.Token
                match loaded with
                | Error SourceUnavailable -> return ReadBackupHistoryOutcome(BackupSourceUnavailable())
                | Error SourceOwnershipConflict -> return ReadBackupHistoryOutcome(BackupNativeOwnershipConflict())
                | Error SourceIdentityUnavailable -> return ReadBackupHistoryOutcome(BackupNativeIdentityUnavailable())
                | Ok(cluster, sourceIdentity) ->
                    let! schedule = get client PostgresBackupNames.Group request.Archive.Placement.NamespaceName
                                        "scheduledbackups" (PostgresBackupNames.scheduledBackup request.Archive.Location.ArchiveId)
                                        deadline.Token
                    if isNull schedule then return ReadBackupHistoryOutcome(BackupHistoryPending())
                    elif not (ownsArchive schedule request.Archive)
                         || not (belongsTo "Cluster" sourceIdentity.Uid schedule) then
                        return ReadBackupHistoryOutcome(BackupNativeOwnershipConflict())
                    else
                        let! result = readReady client request sourceIdentity schedule deadline.Token
                        ct.ThrowIfCancellationRequested()
                        return result
            with
            | error when expired error -> return ReadBackupHistoryOutcome(BackupHistoryRestartRequired())
            | :? JsonException -> return ReadBackupHistoryOutcome(BackupHistoryUnreadable())
            | :? OperationCanceledException when not ct.IsCancellationRequested && deadline.IsCancellationRequested ->
                return ReadBackupHistoryOutcome(BackupHistoryDeadlineReached())
        }
