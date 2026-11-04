namespace Pole.Features.Postgres.Application.Persistence.Credentials

open System.Threading
open MongoDB.Driver
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Postgres.Domain
open Pole.Features.Secrets.Contracts

type PostgresBootstrapRequest =
    { Cluster : PostgresCluster
      Placement : BootstrapSecretPlacement
      Capacity : PostgresDesiredCapacity }

type PostgresBootstrapOutcome =
    | PostgresBootstrapInitialized of PostgresCluster
    | PostgresBootstrapUnavailable of ConsumerSecretReferenceError
    | PostgresBootstrapChanged
    | PostgresBootstrapProtectionUnavailable
    | PostgresBootstrapValuesTooLarge

[<RequireQualifiedAccess>]
module PostgresBootstrapRequests =
    let create (request : PostgresBootstrapRequest) =
        let cluster = request.Cluster
        let bootstrap =
            match cluster.Provisioning with
            | :? PreparedPostgresProvisioning as prepared -> prepared.Bootstrap
            | :? ConfiguredPostgresProvisioning as configured -> configured.Bootstrap
            | _ -> invalidOp "A direct PostgreSQL cluster does not use declarative bootstrap."

        if request.Placement.ResourceGroupId <> cluster.GroupId then
            invalidArg "request" "Bootstrap placement must belong to the PostgreSQL resource group."

        BootstrapSecretRequest(
            cluster.TenantId,
            PostgresManagedSecret.owner cluster.Id,
            BootstrapSecretSource(bootstrap.SourceSecretId, bootstrap.SourceKey),
            request.Placement,
            bootstrap.CredentialId,
            PostgresManagedSecret.name cluster.Id,
            bootstrap.Username)

type PostgresBootstrapInitializer(read : ReadBootstrapSecret, create : CreateBootstrapSecret) =
    member _.InvokeAsync(request : PostgresBootstrapRequest, session : IClientSessionHandle, ct : CancellationToken) =
        task {
            PostgresPersistenceGuards.transaction session "PostgreSQL bootstrap"
            PostgresPersistenceGuards.cluster request.Cluster
            if not (request.Cluster.Lifecycle :? ActivePostgresClusterLifecycle) then
                invalidArg "request" "A deleting PostgreSQL cluster cannot initialize a credential."

            let expected = PostgresBootstrapRequests.create request
            let initialized (actual : BootstrapSecretInitialized) =
                if actual.CredentialId <> expected.CredentialId then
                    PostgresBootstrapUnavailable ConsumerSecretReferenceError.SecretConsumerUnavailable
                else
                    PostgresBootstrapInitialized(request.Cluster.ConfigurePrepared(request.Capacity))

            let! state = read.Invoke(expected, session, ct)
            match state.Value with
            | :? BootstrapSecretInitialized as existing -> return initialized existing
            | :? BootstrapSecretUnavailable as unavailable -> return PostgresBootstrapUnavailable unavailable.Reason
            | :? BootstrapSecretReady when not request.Cluster.IsPrepared ->
                return PostgresBootstrapUnavailable ConsumerSecretReferenceError.SecretConsumerUnavailable
            | :? BootstrapSecretReady as ready ->
                let! created = create.Invoke(ready, session, ct)
                match created.Value with
                | :? BootstrapSecretInitialized as existing -> return initialized existing
                | :? BootstrapSecretUnavailable as unavailable -> return PostgresBootstrapUnavailable unavailable.Reason
                | :? BootstrapSecretChanged -> return PostgresBootstrapChanged
                | :? ManagedSecretEncryptionUnavailable -> return PostgresBootstrapProtectionUnavailable
                | :? BootstrapSecretValuesTooLarge -> return PostgresBootstrapValuesTooLarge
                | _ -> return invalidOp "Unsupported PostgreSQL bootstrap creation outcome."
            | _ -> return invalidOp "Unsupported PostgreSQL bootstrap read outcome."
        }
