// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Events

open MassTransit
open Pole.Features.Events.Abstractions

[<EntityName("pole.postgres/v1/postgres-cluster-deleted")>]
type PostgresClusterDeletedEvent =
    { EventCore: EventCore
      ClusterId: string
      Name: string
      GroupId: string
      ResourceRevision: int64
      Spec: PostgresClusterSpecSnapshot }

    interface IPoleEvent with
        member this.EventCore = this.EventCore
