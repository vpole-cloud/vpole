// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Database;

/// <summary>A host-owned wrapper; scopes migration history to the owning feature.</summary>
public sealed class FeatureScopedMigration(
    string featureId,
    IMigration migration
) : IMigration
{
    public string FeatureId { get; } = featureId;
    public string DurableId => $"{FeatureId}/{Id}";
    public ObjectId Id => migration.Id;
    public string Name => migration.Name;
    public string Description => migration.Description;

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) =>
        migration.Up(context, cancellationToken);
}
