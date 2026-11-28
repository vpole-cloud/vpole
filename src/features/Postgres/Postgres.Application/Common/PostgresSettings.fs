// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Common


type PostgresSupportedVersionsSettings() =
    member val From = 16 with get, set
    member val ``To`` = 17 with get, set

type PostgresImageSettings() =
    member val Server = "" with get, set
    member val Name = "" with get, set

type PostgresSettings() =
    static member SectionName = "Postgres"
    member val SupportedVersions = PostgresSupportedVersionsSettings() with get, set

    member val OperatorImage =
        PostgresImageSettings(Server = "ghcr.io", Name = "cloudnative-pg/postgresql") with get, set


[<RequireQualifiedAccess>]
module PostgresSettings =
    let isSupportedVersion (settings: PostgresSettings) version =
        version >= settings.SupportedVersions.From
        && version <= settings.SupportedVersions.To

    let buildImageReference (image: PostgresImageSettings) version =
        $"{image.Server}/{image.Name}:{version}"
