namespace Pole.Features.Postgres.Application.Credentials

open System.Threading
open System.Threading.Tasks
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application
open Pole.Features.Postgres.Application.Mongo
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
                        PrepareManagedSecretRequest(
                            tenantId,
                            credential.SecretId,
                            PostgresManagedSecret.owner clusterId,
                            credential.Name,
                            Checked.(+) credential.Version 1L,
                            values,
                            credential.KubernetesType),
                        cancellationToken)
                match result.Value with
                | :? PreparedManagedSecret as prepared -> return PostgresSuccess prepared
                | :? ManagedSecretEncryptionUnavailable ->
                    return PostgresError(PostgresCredentialProtectionUnavailable clusterId)
                | _ -> return invalidOp "Unexpected managed Secret preparation outcome."
        }
