// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Kubernetes

[<RequireQualifiedAccess>]
module PostgresBackupNames =
    [<Literal>]
    let PluginName = "barman-cloud.cloudnative-pg.io"

    [<Literal>]
    let Group = "postgresql.cnpg.io"

    [<Literal>]
    let StorageGroup = "barmancloud.cnpg.io"

    [<Literal>]
    let ArchiveLabel = "pole.io/backup-archive-id"

    [<Literal>]
    let ArchivePrefix = "archive-"

    let objectStore archiveId = ArchivePrefix + archiveId

    let scheduledBackup archiveId = "schedule-" + archiveId

    let manualBackup operationId = "backup-" + operationId
