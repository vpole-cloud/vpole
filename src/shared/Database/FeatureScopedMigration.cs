using Pole.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Database;

/// <summary>A host-owned wrapper; scopes migration history to the owning feature.</summary>
public sealed class FeatureScopedMigration(string featureId, IMigration migration) : IMigration
{
    public string FeatureId { get; } = featureId;
    public ObjectId Id => migration.Id;
    public string Name => migration.Name;
    public string Description => migration.Description;
    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) => migration.Up(context, cancellationToken);
    public BsonValue DurableId => $"{FeatureId}/{Id}";
}
