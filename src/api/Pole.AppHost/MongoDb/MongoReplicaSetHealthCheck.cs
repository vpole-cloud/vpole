using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace Pole.AppHost.MongoDb;

public class MongoReplicaSetHealthCheck(
    MongoClientSettings mongoClientSettings
) : IHealthCheck
{
    /// <inheritdoc cref="IHealthCheck.CheckHealthAsync" />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = new())
    {
        var mongoClient = new MongoClient(mongoClientSettings);
        var adminDb = mongoClient.GetDatabase("admin");

        try
        {
            var initiateCmd = new ObjectCommand<ReplicaSetCommandResult>(
                new InitiateReplicaSetCommand(new ReplicaSetInitializationOptions()));
            await adminDb.RunCommandAsync(initiateCmd, cancellationToken: cancellationToken);
        }
        catch (MongoCommandException ex)
        {
            if (ex.CodeName != "AlreadyInitialized")
                return HealthCheckResult.Unhealthy("Failed to initialize MongoDB replica set.", ex);
        }

        try
        {
            var isMasterCmd = new ObjectCommand<ReplicaSetPrimaryResult>(new ReadReplicaSetPrimaryCommand(1));
            var result = await adminDb.RunCommandAsync(isMasterCmd, cancellationToken: cancellationToken);
            if (result.IsPrimary)
                return HealthCheckResult.Healthy();

            return HealthCheckResult.Unhealthy("MongoDB is not the primary node.");
        }
        catch (MongoCommandException ex)
        {
            return HealthCheckResult.Unhealthy("Failed to determine MongoDB primary node.", ex);
        }
    }
}

internal sealed record ReplicaSetInitializationOptions;

internal sealed record InitiateReplicaSetCommand(
    [property: BsonElement("replSetInitiate")]
    ReplicaSetInitializationOptions Options
);

internal sealed record ReadReplicaSetPrimaryCommand(
    [property: BsonElement("isMaster")] int IsMaster
);

[BsonIgnoreExtraElements]
internal sealed record ReplicaSetCommandResult;

[BsonIgnoreExtraElements]
internal sealed record ReplicaSetPrimaryResult
{
    [BsonElement("ismaster")]
    [BsonSerializer(typeof(ReplicaSetPrimaryBooleanSerializer))]
    public bool IsPrimary { get; init; }
}

// Keep the original AsBoolean contract: numeric/string/null replies are not primary-state booleans.
internal sealed class ReplicaSetPrimaryBooleanSerializer : SerializerBase<bool>
{
    public override bool Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) =>
        context.Reader.ReadBoolean();

    public override void Serialize(
        BsonSerializationContext context,
        BsonSerializationArgs args,
        bool value) => context.Writer.WriteBoolean(value);
}
