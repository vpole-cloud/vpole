namespace Pole.Features.Postgres.Application.Persistence.Credentials

open System.Threading
open System.Threading.Tasks
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence
open Pole.Features.Secrets.Contracts

type RotatePostgresCredential =
    delegate of
        tenantId : string *
        clusterId : string *
        credential : ManagedSecretValues *
        password : string *
        cancellationToken : CancellationToken -> Task<PostgresOutcome<PreparedManagedSecret, PostgresCredentialError>>

type PostgresCredentialRotator(prepareSecret : PrepareManagedSecret) =
    member _.InvokeAsync(tenantId, clusterId, credential : ManagedSecretValues, password, cancellationToken : CancellationToken) =
        task {
            let values = credential.Values.SetItem(PostgresManagedSecret.passwordKey, password)
            if PostgresManagedSecret.totalValueBytes values.Values > PostgresManagedSecret.maximumValueBytes then
                return PostgresError(PostgresCredentialValuesTooLarge clusterId)
            else
                let! result =
                    prepareSecret.Invoke(
                        tenantId,
                        credential.SecretId,
                        PostgresManagedSecret.owner clusterId,
                        credential.Name,
                        Checked.(+) credential.Version 1L,
                        values,
                        credential.KubernetesType,
                        cancellationToken)
                match TypedResultInterop.toResult result with
                | Ok prepared -> return PostgresSuccess prepared
                | Error _ -> return PostgresError(PostgresCredentialProtectionUnavailable clusterId)
        }
