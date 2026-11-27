// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Backups.Migrations;

public sealed class BackupArchiveHistoryIndexesMigration : IMigration
{
    public ObjectId Id => new("6af5a10031800609592fefa5");
    public string Name => "Backup archive history recovery and execution indexes";
    public string Description => "Bound due archive scans and tenant archive execution/backfill lookups.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var keys = Builders<BsonDocument>.IndexKeys;
        await context.GetCollection<BsonDocument>("BackupArchives")
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    keys.Ascending("History.Dispatch.NextAttemptAtUtc")
                        .Ascending("History.Dispatch.Lease.ExpiresAtUtc"),
                    new CreateIndexOptions { Name = "IX_BackupArchives_HistoryDispatch" }),
                cancellationToken: cancellationToken);
        // Execution is the only multikey field; combining it with Work._t would index parallel arrays.
        await context.GetCollection<BsonDocument>("BackupOperations")
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    keys.Ascending("TenantId")
                        .Ascending("ArchiveId")
                        .Ascending("Work.Execution._t")
                        .Ascending("Work.ScanId"),
                    new CreateIndexOptions { Name = "IX_BackupOperations_ArchiveExecution" }),
                cancellationToken: cancellationToken);
    }
}
