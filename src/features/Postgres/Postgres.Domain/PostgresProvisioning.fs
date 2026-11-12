namespace Pole.Features.Postgres.Domain

open System
open MongoDB.Bson
open MongoDB.Bson.Serialization.Attributes

type PostgresDesiredCapacity = { Instances: int; Storage: int }

type PostgresBootstrapCredential =
    { [<BsonRepresentation(BsonType.ObjectId)>]
      SourceSecretId: string
      SourceKey: string
      [<BsonRepresentation(BsonType.ObjectId)>]
      CredentialId: string }

    static member Create(sourceSecretId: string, sourceKey: string, credentialId: string) =
        if not (ObjectId.TryParse sourceSecretId |> fst) then
            invalidArg (nameof sourceSecretId) "A bootstrap Secret identity is required."

        if not (ObjectId.TryParse credentialId |> fst) then
            invalidArg (nameof credentialId) "A managed credential identity is required."

        if String.IsNullOrWhiteSpace sourceKey then
            invalidArg (nameof sourceKey) "A bootstrap key is required."

        { SourceSecretId = sourceSecretId
          SourceKey = sourceKey
          CredentialId = credentialId }

[<AbstractClass>]
[<BsonDiscriminator(RootClass = true)>]
[<BsonKnownTypes(typeof<DirectPostgresProvisioning>,
                 typeof<PreparedPostgresProvisioning>,
                 typeof<ConfiguredPostgresProvisioning>)>]
type PostgresProvisioningState() = class end

and [<BsonDiscriminator("direct")>] DirectPostgresProvisioning() =
    inherit PostgresProvisioningState()

and [<BsonDiscriminator("prepared")>] PreparedPostgresProvisioning(bootstrap: PostgresBootstrapCredential) =
    inherit PostgresProvisioningState()

    member _.Bootstrap = bootstrap

and [<BsonDiscriminator("configured")>] ConfiguredPostgresProvisioning(bootstrap: PostgresBootstrapCredential) =
    inherit PostgresProvisioningState()

    member _.Bootstrap = bootstrap
