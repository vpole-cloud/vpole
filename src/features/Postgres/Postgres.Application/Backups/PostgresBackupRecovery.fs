// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Backups

open System
open System.Net
open k8s
open k8s.Autorest
open Pole.Features.Backups.Contracts
open Pole.Features.Postgres.Application.Clusters
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Creation
open Pole.Features.Postgres.Application.Kubernetes
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts

module private RecoveryObservation =
    open PostgresBackupObjects

    type Progress = Pending | Completed of string
    type Failure = IdentityConflict | PlacementChanged | TargetMismatch | RecoveryFailed | NativeIdentityUnavailable

    let matchesOrigin (request: ObserveBackupRestoreRequest) (cluster: PostgresCluster) =
        let targetMatches (originTarget: PostgresRecoveryTarget) =
            match originTarget, request.Target with
            | (:? PostgresCopyRecoveryTarget as copy), (:? BackupCopyRecoveryTarget as selected) ->
                copy.BackupId = selected.BackupId
            | (:? PostgresTimeRecoveryTarget as time), (:? BackupTimeRecoveryTarget as selected) ->
                time.TargetTimeUtc = selected.TargetTime.UtcDateTime
            | _ -> false
        cluster.TenantId = request.Archive.Source.TenantId
        && cluster.Id = request.ResourceId
        && cluster.GroupId = request.Archive.Placement.ResourceGroupId
        && match cluster.Spec.Bootstrap with
           | :? RecoveryPostgresBootstrap as recovery ->
               let origin = recovery.Origin
               origin.OperationId = request.OperationId
               && origin.ArchiveId = request.Archive.Location.ArchiveId
               && origin.ServerName = request.Archive.Location.ServerName
               && targetMatches origin.Target
           | _ -> false

    let matchesNativeTarget (request: ObserveBackupRestoreRequest) native =
        let recovery = field native ["spec"; "bootstrap"; "recovery"]
        let targetMatches =
            match request.Target with
            | :? BackupTimeRecoveryTarget as time ->
                timestamp recovery ["recoveryTarget"; "targetTime"] = Nullable(time.TargetTime.ToUniversalTime())
            | :? BackupCopyRecoveryTarget as copy ->
                let immediate = value recovery ["recoveryTarget"; "targetImmediate"]
                value recovery ["recoveryTarget"; "backupID"] = copy.BackupId
                && String.Equals(immediate, "true", StringComparison.OrdinalIgnoreCase)
            | _ -> invalidOp "Unsupported backup recovery target."
        targetMatches && value recovery ["source"] = "origin"

    let nativeStatus (client: IKubernetes) (request: ObserveBackupRestoreRequest) (cluster: PostgresCluster)
        (placement: PlacementSummaryDto) ct =
        task {
            let ns = request.Archive.Placement.NamespaceName
            let name = PostgresClusterKubernetesBuilder.buildResourceName cluster.Id
            let! native = get client PostgresBackupNames.Group ns "clusters" name ct
            if isNull native then return Ok Pending
            else
                let labels =
                    [ "pole.io/tenant-id", cluster.TenantId
                      "pole.io/resource-id", cluster.Id
                      "pole.io/runtime-cell-id", placement.RuntimeCellId
                      "pole.io/resource-deployment-id", placement.DeploymentId ]
                if not (labelsMatch native labels) then return Error IdentityConflict
                elif not (matchesNativeTarget request native) then return Error TargetMismatch
                else
                    match identity native with
                    | None -> return Error NativeIdentityUnavailable
                    | Some nativeIdentity ->
                        let ready = value (condition native "Ready") ["status"] = "True"
                        let primary = value native ["status"; "currentPrimary"]
                        if placement.Status = ResourceDeploymentStatus.Applied
                           && placement.DesiredGeneration = placement.ObservedGeneration
                           && not (String.IsNullOrEmpty primary) && ready then
                            return Ok(Completed cluster.Id)
                        else
                            let! jobs = client.BatchV1.ListNamespacedJobAsync(
                                ns, labelSelector = ("cnpg.io/cluster=" + name + ",cnpg.io/jobRole=full-recovery"),
                                cancellationToken = ct)
                            let failed =
                                jobs.Items |> Seq.exists (fun job ->
                                    not (isNull job.Metadata.OwnerReferences)
                                    && (job.Metadata.OwnerReferences |> Seq.exists (fun owner ->
                                        owner.Uid = nativeIdentity.Uid && owner.Kind = "Cluster"))
                                    && not (isNull job.Status) && not (isNull job.Status.Conditions)
                                    && (job.Status.Conditions |> Seq.exists (fun condition ->
                                        (condition.Type = "Failed" || condition.Type = "FailureTarget")
                                        && condition.Status = "True")))
                            return if failed then Error RecoveryFailed else Ok Pending
        }

    let toObservation = function
        | Ok Pending -> ObserveBackupRestoreOutcome(BackupRestoreInProgress())
        | Ok(Completed id) -> ObserveBackupRestoreOutcome(BackupRestoreCompleted(id))
        | Error IdentityConflict -> ObserveBackupRestoreOutcome(BackupRestoreIdentityConflict())
        | Error PlacementChanged -> ObserveBackupRestoreOutcome(BackupRestorePlacementChanged())
        | Error TargetMismatch -> ObserveBackupRestoreOutcome(BackupRestoreTargetMismatch())
        | Error RecoveryFailed -> ObserveBackupRestoreOutcome(BackupRestoreRecoveryFailed())
        | Error NativeIdentityUnavailable -> ObserveBackupRestoreOutcome(BackupNativeIdentityUnavailable())

    let toCreation = function
        | Ok Pending -> RestoreBackupOutcome(BackupRestoreInProgress())
        | Ok(Completed id) -> RestoreBackupOutcome(BackupRestoreCompleted(id))
        | Error IdentityConflict -> RestoreBackupOutcome(BackupRestoreIdentityConflict())
        | Error PlacementChanged -> RestoreBackupOutcome(BackupRestorePlacementChanged())
        | Error TargetMismatch -> RestoreBackupOutcome(BackupRestoreTargetMismatch())
        | Error RecoveryFailed -> RestoreBackupOutcome(BackupRestoreRecoveryFailed())
        | Error NativeIdentityUnavailable -> RestoreBackupOutcome(BackupNativeIdentityUnavailable())

type PostgresBackupRecovery
    (load: LoadPostgresClusterById, placements: LoadResourceDeployment, creator: PostgresClusterCreator,
     settings: PostgresSettings, recordTarget: RecordRestoreTargetCreation) =

    let resourceType = PostgresResourceDeployment.ResourceType

    let observeCluster client (request: ObserveBackupRestoreRequest) cluster ct =
        task {
            if not (RecoveryObservation.matchesOrigin request cluster) then
                return Error RecoveryObservation.IdentityConflict
            else
                let source = request.Archive.Placement
                let placementRequest = LoadResourceDeploymentRequest(cluster.TenantId, resourceType, cluster.Id)
                let! placement = placements.Invoke(placementRequest, ct)
                if isNull placement || placement.RuntimeCellId <> source.RuntimeCellId
                   || placement.IsolationScopeId <> source.IsolationScopeId
                   || placement.DeploymentTargetId <> source.DeploymentTargetId then
                    return Error RecoveryObservation.PlacementChanged
                else return! RecoveryObservation.nativeStatus client request cluster placement ct
        }

    let observationRequest (request: RestoreBackupRequest) =
        ObserveBackupRestoreRequest(request.OperationId, request.Archive, request.Resource.Id, request.Target)

    let createTarget client (request: RestoreBackupRequest) origin version ct =
        task {
            let placement = request.Archive.Placement
            let configuration =
                { Name = request.Resource.Name
                  GroupId = placement.ResourceGroupId
                  Version = version
                  Instances = Some request.Archive.Service.Instances
                  Storage = Some(request.Resource.StorageGiB.GetValueOrDefault(request.Archive.Service.StorageGiB))
                  StorageProfileId = placement.StorageProfileId
                  ComputeProfileId = Some placement.ComputeProfileId
                  Placement = Some(PlacementRequest(placement.IsolationScopeId, placement.DeploymentTargetId)) }
            let command =
                { TenantId = request.Archive.Source.TenantId
                  ResourceId = request.Resource.Id
                  Configuration = configuration
                  Authority =
                    PostgresCreationAuthority.Recovery(
                        origin, { RuntimeCellId = placement.RuntimeCellId }, recordTarget) }
            let! created = creator.CreateAsync(command, ct)
            match created with
            | PostgresCreationOutcome.Created _ -> return RestoreBackupOutcome(BackupRestoreInProgress())
            | PostgresCreationOutcome.Rejected(CreateNameAlreadyExists _)
            | PostgresCreationOutcome.Rejected(CreateState PostgresClusterRestoreTargetAlreadyCreated) ->
                // The receipt and resource can be committed by another lease while this attempt is in flight.
                let! winner = load.Invoke(command.TenantId, request.Resource.Id, ct)
                match winner with
                | Some cluster ->
                    let! result = observeCluster client (observationRequest request) cluster ct
                    return RecoveryObservation.toCreation result
                | None ->
                    return
                        match created with
                        | PostgresCreationOutcome.Rejected(CreateState PostgresClusterRestoreTargetAlreadyCreated) ->
                            RestoreBackupOutcome(BackupRestoreResourceDeleted())
                        | PostgresCreationOutcome.Rejected(CreateNameAlreadyExists _) ->
                            RestoreBackupOutcome(BackupRestoreNameAlreadyExists())
                        | _ -> invalidOp "Only a conflicting creation can require its winner."
            | PostgresCreationOutcome.Rejected(CreateQuota _) ->
                return RestoreBackupOutcome(BackupRestoreQuotaRejected())
            | PostgresCreationOutcome.Rejected(CreateTarget _) ->
                return RestoreBackupOutcome(BackupRestoreTargetRejected())
            | PostgresCreationOutcome.Rejected(CreateCompute _) ->
                return RestoreBackupOutcome(BackupRestoreComputeRejected())
            | PostgresCreationOutcome.Rejected(CreateStorage _) ->
                return RestoreBackupOutcome(BackupRestoreStorageRejected())
            | PostgresCreationOutcome.Rejected(CreateCredential _) ->
                return RestoreBackupOutcome(BackupRestoreCredentialRejected())
            | PostgresCreationOutcome.Rejected(CreateState PostgresClusterRestoreOperationUnavailable) ->
                return RestoreBackupOutcome(BackupRestoreOperationUnavailable())
            | PostgresCreationOutcome.Rejected(CreateState(PostgresClusterComputeRuntimeClassUnsupported _)) ->
                return RestoreBackupOutcome(BackupRestoreComputeRejected())
            | PostgresCreationOutcome.Rejected(CreateState PostgresClusterRestorePlacementChanged)
            | PostgresCreationOutcome.Rejected(CreateDeployment _)
            | PostgresCreationOutcome.Rejected(CreateRegistration _) ->
                return RestoreBackupOutcome(BackupRestorePlacementRejected())
        }

    let prepareTarget client (request: RestoreBackupRequest) ct =
        task {
            let unsupportedPrecision =
                match request.Target with
                | :? BackupTimeRecoveryTarget as time -> time.TargetTime.UtcTicks % 10L <> 0L
                | :? BackupCopyRecoveryTarget -> false
                | _ -> invalidOp "Unsupported backup recovery target."
            let validVersion, version = Int32.TryParse(request.Archive.Service.Version)
            if unsupportedPrecision then return RestoreBackupOutcome(BackupRestoreTimePrecisionUnsupported())
            elif request.Resource.StorageGiB.HasValue
                 && request.Resource.StorageGiB.Value < request.Archive.Service.StorageGiB then
                return RestoreBackupOutcome(BackupRestoreStorageTooSmall())
            elif not validVersion || not (PostgresSettings.isSupportedVersion settings version) then
                return RestoreBackupOutcome(BackupRestoreVersionUnsupported())
            else
                let! stored = PostgresBackupArchive.ensureStore client request.Archive request.Projection ct
                match stored with
                | Error PostgresBackupArchive.OwnershipConflict ->
                    return RestoreBackupOutcome(BackupNativeOwnershipConflict())
                | Error PostgresBackupArchive.IdentityUnavailable ->
                    return RestoreBackupOutcome(BackupNativeIdentityUnavailable())
                | Ok _ ->
                    let target: PostgresRecoveryTarget =
                        match request.Target with
                        | :? BackupTimeRecoveryTarget as time -> PostgresTimeRecoveryTarget(time.TargetTime.UtcDateTime)
                        | :? BackupCopyRecoveryTarget as copy -> PostgresCopyRecoveryTarget(copy.BackupId)
                        | _ -> invalidOp "Unsupported backup recovery target."
                    let origin =
                        { OperationId = request.OperationId
                          ArchiveId = request.Archive.Location.ArchiveId
                          ServerName = request.Archive.Location.ServerName
                          Target = target }
                    return! createTarget client request origin version ct
        }

    member _.ObserveAsync(client, request: ObserveBackupRestoreRequest, ct) =
        task {
            let! existing = load.Invoke(request.Archive.Source.TenantId, request.ResourceId, ct)
            match existing with
            | None -> return ObserveBackupRestoreOutcome(BackupRestoreTargetMissing())
            | Some cluster ->
                let! result = observeCluster client request cluster ct
                return RecoveryObservation.toObservation result
        }

    member _.CreateAsync(client, request: RestoreBackupRequest, ct) =
        task {
            let! existing = load.Invoke(request.Archive.Source.TenantId, request.Resource.Id, ct)
            match existing with
            | Some cluster ->
                let! result = observeCluster client (observationRequest request) cluster ct
                return RecoveryObservation.toCreation result
            | None ->
                try
                    return! prepareTarget client request ct
                with
                | :? HttpOperationException as error when error.Response.StatusCode = HttpStatusCode.Conflict ->
                    return RestoreBackupOutcome(BackupNativeWriteConflict())
        }
