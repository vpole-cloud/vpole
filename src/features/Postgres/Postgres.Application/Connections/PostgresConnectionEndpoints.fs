// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Connections

[<RequireQualifiedAccess>]
module internal PostgresConnectionEndpoints =
    [<Literal>]
    let ReadWriteId = "postgres"

    [<Literal>]
    let TcpPort = 5432

    let buildReadWriteServiceName (resourceId: string) = $"pg-{resourceId}-rw"
