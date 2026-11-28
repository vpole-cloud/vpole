// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Declarations

open System.Threading
open System.Threading.Tasks
open Pole.Database
open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.GitOps.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.StorageProfiles.Contracts

type private PostgresCatalogReferences =
    { TargetId: string
      ComputeId: string
      StorageId: string }

type PostgresDeclarationReferences
    (
        git: IGitOpsMutations,
        transactions: MongoRunTransaction,
        resolveTarget: ResolveNamedDeploymentTarget,
        resolveCompute: ResolveNamedComputeProfile,
        resolveStorage: ResolveNamedStorageProfile
    ) =

    let pin (operation: GitOperationInput) address id ct =
        task {
            let! pinned =
                git.PinReferenceAsync(
                    PinGitReferenceRequest(operation.Context, operation.ResourceId, GitReferenceTarget(address, id)),
                    ct
                )

            match pinned.Value with
            | :? GitReferencePinned as accepted -> return Ok accepted.ResourceId
            | :? StaleGitExecution as stale -> return Error(stale :> IDeclarationFailure)
            | _ -> return invalidOp "Unsupported PostgreSQL reference pinning outcome."
        }

    member _.PrepareAsync
        (operation: GitOperationInput, ct: CancellationToken)
        : Task<PrepareDeclarationReferencesOutcome> =
        task {
            let spec = DeclarationJson.Read<PostgresDeclaration> operation.Declaration.Spec

            let targetAddress =
                ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget)

            let computeAddress =
                ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name)

            let storageAddress = ResourceAddress("StorageProfile", spec.Storage.Profile.Name)

            let! pinned =
                transactions.InvokeOutcomeAsync<PostgresCatalogReferences>(
                    (fun transaction token ->
                        task {
                            let! target =
                                git.ResolveAcceptedAsync(
                                    ResolveAcceptedGitReferenceRequest(
                                        operation.Context,
                                        operation.ResourceId,
                                        targetAddress
                                    ),
                                    transaction.Session,
                                    token
                                )

                            let! compute =
                                git.ResolveAcceptedAsync(
                                    ResolveAcceptedGitReferenceRequest(
                                        operation.Context,
                                        operation.ResourceId,
                                        computeAddress
                                    ),
                                    transaction.Session,
                                    token
                                )

                            let! storage =
                                git.ResolveAcceptedAsync(
                                    ResolveAcceptedGitReferenceRequest(
                                        operation.Context,
                                        operation.ResourceId,
                                        storageAddress
                                    ),
                                    transaction.Session,
                                    token
                                )

                            return
                                transaction.Commit
                                    { TargetId = target
                                      ComputeId = compute
                                      StorageId = storage }
                        }),
                    ct
                )

            let! target =
                task {
                    if not (isNull pinned.TargetId) then
                        return Ok pinned.TargetId
                    else
                        let! found =
                            resolveTarget.Invoke(
                                NamedDeploymentTargetRequest(
                                    operation.Context.TenantId,
                                    PostgresResourceDeployment.ResourceType,
                                    targetAddress.Name
                                ),
                                ct
                            )

                        match found.Value with
                        | :? DeploymentTargetLookupDto as target -> return! pin operation targetAddress target.Id ct
                        | _ -> return PostgresPlanning.rejected PostgresDeclarationError.DeploymentTargetUnavailable
                }

            let compute targetId =
                task {
                    if not (isNull pinned.ComputeId) then
                        return Ok targetId
                    else
                        let! found =
                            resolveCompute.Invoke(
                                NamedComputeProfileRequest(
                                    operation.Context.TenantId,
                                    PostgresResourceDeployment.ResourceType,
                                    computeAddress.Name,
                                    targetId,
                                    spec.Placement.ComputeProfile.Revision
                                ),
                                ct
                            )

                        match found.Value with
                        | :? ResolvedComputeProfile as profile ->
                            let! result = pin operation computeAddress profile.ComputeProfileId ct
                            return result |> Result.map (fun _ -> targetId)
                        | _ -> return PostgresPlanning.rejected PostgresDeclarationError.ComputeProfileUnavailable
                }

            let storage targetId =
                task {
                    if not (isNull pinned.StorageId) then
                        return Ok()
                    else
                        let! found =
                            resolveStorage.Invoke(
                                NamedStorageProfileRequest(
                                    operation.Context.TenantId,
                                    PostgresResourceDeployment.ResourceType,
                                    storageAddress.Name,
                                    targetId,
                                    spec.Storage.SizeGb,
                                    spec.Storage.Profile.Revision
                                ),
                                ct
                            )

                        match found.Value with
                        | :? ResolvedStorageProfile as profile ->
                            let! result = pin operation storageAddress profile.ProfileId ct
                            return result |> Result.map ignore
                        | _ -> return PostgresPlanning.rejected PostgresDeclarationError.StorageProfileUnavailable
                }

            let! result =
                Task.FromResult target
                |> PostgresPlanning.bind compute
                |> PostgresPlanning.bind storage

            match result with
            | Ok() -> return PrepareDeclarationReferencesOutcome(DeclarationReferencesPrepared())
            | Error(:? StaleGitExecution as stale) -> return PrepareDeclarationReferencesOutcome(stale)
            | Error failure -> return PrepareDeclarationReferencesOutcome(DeclarationReadBlocked failure)
        }
