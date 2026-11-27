// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Workloads;

namespace Pole.Features.Backups.Application;

public static class BackupResourceTypes
{
    public static readonly ResourceTypeId Destination = ResourceTypeId.Create("pole/backup-destination");
}

public static class BackupDeclarationKinds
{
    public const string Destination = "BackupDestination";
    public const string Policy = "BackupPolicy";
}
