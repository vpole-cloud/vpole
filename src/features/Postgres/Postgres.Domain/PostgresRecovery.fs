// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Domain

open System
open MongoDB.Bson
open MongoDB.Bson.Serialization.Attributes

[<AbstractClass>]
[<BsonDiscriminator(RootClass = true)>]
[<BsonKnownTypes(typeof<PostgresCopyRecoveryTarget>, typeof<PostgresTimeRecoveryTarget>)>]
type PostgresRecoveryTarget() = class end

and [<BsonDiscriminator("copy")>] PostgresCopyRecoveryTarget(backupId: string) =
    inherit PostgresRecoveryTarget()

    do
        if String.IsNullOrWhiteSpace backupId then
            invalidArg (nameof backupId) "A backup identity is required."

    member _.BackupId = backupId

    override _.Equals(other) =
        match other with
        | :? PostgresCopyRecoveryTarget as target -> backupId = target.BackupId
        | _ -> false

    override _.GetHashCode() = hash backupId

and [<BsonDiscriminator("time")>] PostgresTimeRecoveryTarget(targetTimeUtc: DateTime) =
    inherit PostgresRecoveryTarget()

    do
        if targetTimeUtc.Kind <> DateTimeKind.Utc then
            invalidArg (nameof targetTimeUtc) "The recovery target must be UTC."

    [<BsonDateTimeOptions(Representation = BsonType.Document)>]
    member _.TargetTimeUtc = targetTimeUtc

    override _.Equals(other) =
        match other with
        | :? PostgresTimeRecoveryTarget as target -> targetTimeUtc = target.TargetTimeUtc
        | _ -> false

    override _.GetHashCode() = hash targetTimeUtc

type PostgresRecoveryOrigin =
    { OperationId: string
      ArchiveId: string
      ServerName: string
      Target: PostgresRecoveryTarget }

[<AbstractClass>]
[<BsonDiscriminator(RootClass = true)>]
[<BsonKnownTypes(typeof<FreshPostgresBootstrap>, typeof<RecoveryPostgresBootstrap>)>]
type PostgresClusterBootstrap() = class end

and [<BsonDiscriminator("fresh")>] FreshPostgresBootstrap() =
    inherit PostgresClusterBootstrap()

    override _.Equals(other) = other :? FreshPostgresBootstrap

    override _.GetHashCode() = hash "fresh"

and [<BsonDiscriminator("recovery")>] RecoveryPostgresBootstrap(origin: PostgresRecoveryOrigin) =
    inherit PostgresClusterBootstrap()

    do
        if isNull (box origin) || isNull (box origin.Target) then
            invalidArg (nameof origin) "A recovery origin and target are required."

    member _.Origin = origin

    override _.Equals(other) =
        match other with
        | :? RecoveryPostgresBootstrap as bootstrap -> origin = bootstrap.Origin
        | _ -> false

    override _.GetHashCode() = hash origin
