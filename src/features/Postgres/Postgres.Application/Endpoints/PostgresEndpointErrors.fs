namespace Pole.Features.Postgres.Application.Endpoints

open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Application.Credentials
open Pole.Features.Postgres.Application
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Application.Lifecycle
open Pole.Features.Postgres.Application.Clusters
open Pole.Features.Quotas.Contracts
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceGroups.Contracts
open Pole.Features.StorageProfiles.Contracts

type CreatePostgresClusterStateError =
    | PostgresClusterComputeRuntimeClassUnsupported of deploymentTargetId : string

type CreatePostgresClusterError =
    | CreateState of CreatePostgresClusterStateError
    | CreateTarget of DeploymentTargetNewWorkloadAdmissionError
    | CreateCompute of ComputeProfileResolutionError
    | CreateStorage of StorageProfileResolutionError
    | CreateCredential of PostgresCredentialError
    | CreateDeployment of CreateResourceDeploymentError
    | CreateRegistration of ResourceRegistrationMutationError
    | CreateNameAlreadyExists of PostgresClusterNameAlreadyExists
    | CreateQuota of QuotaExecutionError

type DeletePostgresClusterStateError =
    | PostgresClusterDeletionNotFound of clusterId : string
    | PostgresClusterDeletionResourceDeploymentMissing of clusterId : string

type DeletePostgresClusterError =
    | DeletePostgresClusterState of DeletePostgresClusterStateError
    | DeletePostgresClusterCompute of LoadCurrentResourceDeploymentComputeError
    | DeletePostgresClusterPersistence of PostgresClusterDeletionConflict
    | DeletePostgresClusterDeployment of ResourceDeploymentMutationError
    | DeletePostgresClusterQuota of QuotaExecutionError

type UpdatePostgresClusterStateError =
    | PostgresClusterUpdateNotFound of clusterId : string
    | PostgresClusterUpdateUnavailable of clusterId : string
    | PostgresClusterStatefulSetInstancesUnsupported of clusterId : string
    | PostgresClusterStatefulSetStorageResizeUnsupported of clusterId : string
    | PostgresClusterComputeRuntimeClassUnsupported of clusterId : string

type UpdatePostgresClusterError =
    | UpdateState of UpdatePostgresClusterStateError
    | UpdateCompute of ComputeProfileResolutionError
    | UpdateCurrentCompute of LoadCurrentResourceDeploymentComputeError
    | UpdateStorage of StorageProfileResolutionError
    | UpdateDeployment of ResourceDeploymentMutationError
    | UpdateQuota of QuotaExecutionError
    | UpdateConflict of PostgresClusterUpdateConflict

type UpdatePostgresClusterPasswordStateError =
    | PostgresClusterPasswordNotFound of clusterId : string
    | PostgresClusterPasswordRotationUnavailable of clusterId : string
    | PostgresClusterPasswordRotationUnsupported of clusterId : string

type UpdatePostgresClusterPasswordError =
    | PasswordState of UpdatePostgresClusterPasswordStateError
    | PasswordCompute of LoadCurrentResourceDeploymentComputeError
    | PasswordCredential of PostgresCredentialError
    | PasswordDeployment of ResourceDeploymentMutationError

type GetPostgresClusterConnectionStringStateError =
    | PostgresClusterConnectionStringNotFound of clusterId : string
    | PostgresClusterConnectionStringDeleting of clusterId : string
    | PostgresClusterDeploymentNotReady of clusterId : string
    | PostgresClusterConnectionEndpointUnavailable of clusterId : string

type GetPostgresClusterConnectionStringError =
    | ConnectionStringState of GetPostgresClusterConnectionStringStateError
    | ConnectionStringCredentials of PostgresCredentialError

type GetPostgresClusterByIdError =
    | PostgresClusterNotFound of clusterId : string
    | PostgresClusterDeploymentUnavailable of clusterId : string
