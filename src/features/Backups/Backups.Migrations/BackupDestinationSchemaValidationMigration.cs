// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;
using static Pole.Features.Backups.Migrations.BackupCollectionSchema;

namespace Pole.Features.Backups.Migrations;

public sealed class BackupDestinationSchemaValidationMigration : IMigration
{
    public ObjectId Id => new("6af5a10031800609592fefa1");
    public string Name => "Backup destination document validation";
    public string Description => "Require tenant ownership, logical credential references and configuration revisions.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) => ApplyAsync(
        context,
        "BackupDestinations",
        Object(
            ("_id", Text()),
            ("TenantId", Text()),
            ("Name", Text()),
            ("ResourceGroupId", Text()),
            ("IsolationScopeId", Text()),
            ("Storage", Storage()),
            ("Revision", Number("long", 1)),
            ("ReferenceFence", Number("long", 0))),
        cancellationToken);
}
