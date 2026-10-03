namespace Pole.Features.Postgres.Application.Common

open System
open System.Collections.Generic
open System.Net
open Pole.ASP.Helpers
open Pole.Features.ComputeProfiles.Contracts.HttpMapping
open Pole.Features.DeploymentTargets.Contracts.HttpMapping
open Pole.Features.Postgres.Application.Endpoints
open Pole.Features.Postgres.Application.Persistence.Credentials
open Pole.Features.Postgres.Application.Persistence.Lifecycle
open Pole.Features.Postgres.Application.Persistence.Mutations
open Pole.Features.Quotas.Contracts.HttpMapping
open Pole.Features.ResourceDeployments.Contracts.HttpMapping
open Pole.Features.ResourceGroups.Contracts.HttpMapping
open Pole.Features.StorageProfiles.Contracts.HttpMapping

[<RequireQualifiedAccess>]
module PostgresHttpMappings =
    let private problem status code title clusterId =
        let extensions = Dictionary<string, obj>()
        extensions.Add("clusterId", clusterId)
        HttpProblem(
            int status,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            extensions = extensions)

    let private mapCreateState (error : CreatePostgresClusterStateError) =
        match error with
        | CreatePostgresClusterStateError.PostgresClusterComputeRuntimeClassUnsupported targetId ->
            let extensions = Dictionary<string, obj>()
            extensions.Add("deploymentTargetId", targetId)
            HttpProblem(
                int HttpStatusCode.UnprocessableEntity,
                PoleProblemTypeId.FromCode("postgres.compute.runtime-class-unsupported"),
                "CloudNativePG compute profiles cannot specify a runtime class",
                "postgres.compute.runtime-class-unsupported",
                extensions = extensions)

    let private mapUpdateState (error : UpdatePostgresClusterStateError) =
        match error with
        | PostgresClusterUpdateNotFound clusterId ->
            problem HttpStatusCode.NotFound "postgres.not-found" "PostgreSQL cluster not found" clusterId
        | PostgresClusterUpdateUnavailable clusterId ->
            problem HttpStatusCode.Conflict "postgres.update-unavailable" "PostgreSQL cluster cannot be updated in its current lifecycle" clusterId
        | PostgresClusterStatefulSetInstancesUnsupported clusterId ->
            problem HttpStatusCode.UnprocessableEntity "postgres.instances.stateful-set-unsupported" "StatefulSet PostgreSQL supports exactly one instance" clusterId
        | UpdatePostgresClusterStateError.PostgresClusterComputeRuntimeClassUnsupported clusterId ->
            problem HttpStatusCode.UnprocessableEntity "postgres.compute.runtime-class-unsupported" "CloudNativePG compute profiles cannot specify a runtime class" clusterId

    let private mapPasswordState (error : UpdatePostgresClusterPasswordStateError) =
        match error with
        | PostgresClusterPasswordNotFound clusterId ->
            problem HttpStatusCode.NotFound "postgres.not-found" "PostgreSQL cluster not found" clusterId
        | PostgresClusterPasswordRotationUnavailable clusterId ->
            problem HttpStatusCode.Conflict "postgres.password.rotation-unavailable" "Credentials cannot be rotated while the PostgreSQL cluster is being deleted" clusterId
        | PostgresClusterPasswordRotationUnsupported clusterId ->
            problem HttpStatusCode.Conflict "postgres.password.rotation-unsupported" "Password rotation is not supported for StatefulSet PostgreSQL deployments" clusterId

    let private mapDeleteState (error : DeletePostgresClusterStateError) =
        match error with
        | PostgresClusterDeletionNotFound clusterId ->
            problem HttpStatusCode.NotFound "postgres.not-found" "PostgreSQL cluster not found" clusterId
        | PostgresClusterDeletionResourceDeploymentMissing clusterId ->
            problem HttpStatusCode.Conflict "postgres.deletion-deployment-missing" "Deleting PostgreSQL cluster has no resource deployment" clusterId

    let private mapGetById (error : GetPostgresClusterByIdError) =
        match error with
        | PostgresClusterNotFound clusterId ->
            problem HttpStatusCode.NotFound "postgres.not-found" "PostgreSQL cluster not found" clusterId
        | PostgresClusterDeploymentUnavailable clusterId ->
            problem HttpStatusCode.Conflict "postgres.deployment-unavailable" "The PostgreSQL resource deployment is unavailable" clusterId

    let private mapConnectionStringState (error : GetPostgresClusterConnectionStringStateError) =
        match error with
        | PostgresClusterConnectionStringNotFound clusterId ->
            problem HttpStatusCode.NotFound "postgres.not-found" "PostgreSQL cluster not found" clusterId
        | PostgresClusterConnectionStringDeleting clusterId ->
            problem HttpStatusCode.Conflict "postgres.connection-string.deleting" "A connection string is unavailable while the PostgreSQL cluster is being deleted" clusterId
        | PostgresClusterDeploymentNotReady clusterId ->
            problem HttpStatusCode.Conflict "postgres.connection-string.deployment-not-ready" "The PostgreSQL deployment has not applied its current desired generation" clusterId
        | PostgresClusterConnectionEndpointUnavailable clusterId ->
            problem HttpStatusCode.Conflict "postgres.connection-string.endpoint-unavailable" "The applied PostgreSQL deployment has no connection endpoint" clusterId

    let private mapCredential (error : PostgresCredentialError) =
        match error with
        | PostgresCredentialNotFound clusterId ->
            problem HttpStatusCode.NotFound "postgres.credentials.not-found" "PostgreSQL credentials were not found" clusterId
        | PostgresCredentialProtectionUnavailable clusterId ->
            problem HttpStatusCode.ServiceUnavailable "postgres.credentials.protection-unavailable" "PostgreSQL credential protection is unavailable" clusterId
        | PostgresCredentialRotationConflict clusterId ->
            problem HttpStatusCode.Conflict "postgres.credentials.concurrent-change" "PostgreSQL credentials changed concurrently" clusterId
        | PostgresCredentialReferenceRejected clusterId ->
            problem HttpStatusCode.Conflict "postgres.credentials.reference-rejected" "PostgreSQL managed credential ownership or placement changed" clusterId
        | PostgresCredentialValuesTooLarge clusterId ->
            problem HttpStatusCode.BadRequest "postgres.credentials.values-too-large" "The combined UTF-8 credential values must not exceed 64 KiB" clusterId

    let private mapUpdateConflict (error : PostgresClusterUpdateConflict) =
        problem HttpStatusCode.Conflict "postgres.update.concurrent-change" "PostgreSQL cluster changed concurrently" error.ClusterId

    let private mapDeletionConflict (error : PostgresClusterDeletionConflict) =
        problem HttpStatusCode.Conflict "postgres.deletion.concurrent-change" "PostgreSQL cluster changed concurrently while deletion was being accepted" error.ClusterId

    let private mapNameAlreadyExists (error : PostgresClusterNameAlreadyExists) =
        let extensions = Dictionary<string, obj>()
        extensions.Add("name", error.Name)
        HttpProblem(
            int HttpStatusCode.Conflict,
            PoleProblemTypeId.FromCode("postgres.name-already-exists"),
            "PostgreSQL cluster name already exists in this tenant",
            "postgres.name-already-exists",
            extensions = extensions)

    let mapCreateError (error : CreatePostgresClusterError) =
        match error with
        | CreateState value -> mapCreateState value
        | CreateTarget value -> DeploymentTargetContractHttpMappings.MapNewWorkloadAdmissionError(value)
        | CreateCompute value -> ComputeProfileContractHttpMappings.MapResolutionError(value)
        | CreateStorage value -> StorageProfileContractHttpMappings.MapResolutionError(value)
        | CreateCredential value -> mapCredential value
        | CreateDeployment value -> ResourceDeploymentContractHttpMappings.MapDeploymentError(value)
        | CreateRegistration value -> ResourceGroupContractHttpMappings.MapRegistrationMutationError(value)
        | CreateNameAlreadyExists value -> mapNameAlreadyExists value
        | CreateQuota value -> QuotaContractHttpMappings.MapExecutionError(value)

    let mapDeleteError (error : DeletePostgresClusterError) =
        match error with
        | DeletePostgresClusterState value -> mapDeleteState value
        | DeletePostgresClusterCompute value -> ResourceDeploymentContractHttpMappings.MapComputeError(value)
        | DeletePostgresClusterPersistence value -> mapDeletionConflict value
        | DeletePostgresClusterDeployment value -> ResourceDeploymentContractHttpMappings.MapMutationError(value)
        | DeletePostgresClusterQuota value -> QuotaContractHttpMappings.MapExecutionError(value)

    let mapUpdateError (error : UpdatePostgresClusterError) =
        match error with
        | UpdateState value -> mapUpdateState value
        | UpdateCompute value -> ComputeProfileContractHttpMappings.MapResolutionError(value)
        | UpdateCurrentCompute value -> ResourceDeploymentContractHttpMappings.MapComputeError(value)
        | UpdateStorage value -> StorageProfileContractHttpMappings.MapResolutionError(value)
        | UpdateDeployment value -> ResourceDeploymentContractHttpMappings.MapMutationError(value)
        | UpdateQuota value -> QuotaContractHttpMappings.MapExecutionError(value)
        | UpdateConflict value -> mapUpdateConflict value

    let mapPasswordError (error : UpdatePostgresClusterPasswordError) =
        match error with
        | PasswordState value -> mapPasswordState value
        | PasswordCompute value -> ResourceDeploymentContractHttpMappings.MapComputeError(value)
        | PasswordCredential value -> mapCredential value
        | PasswordDeployment value -> ResourceDeploymentContractHttpMappings.MapMutationError(value)

    let mapConnectionStringError (error : GetPostgresClusterConnectionStringError) =
        match error with
        | ConnectionStringState value -> mapConnectionStringState value
        | ConnectionStringCredentials value -> mapCredential value

    let mapGetByIdError (error : GetPostgresClusterByIdError) = mapGetById error
