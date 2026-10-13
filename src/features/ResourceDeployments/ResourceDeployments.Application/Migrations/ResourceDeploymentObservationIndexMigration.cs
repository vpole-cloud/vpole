using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Persistence.Observation;

namespace Pole.Features.ResourceDeployments.Application.Migrations;

public sealed class ResourceDeploymentObservationIndexMigration : IMigration
{
    public ObjectId Id => new("6acc0000b213917a38c38107");
    public string Name => "Resource deployment observation demand and snapshot expiry";
    public string Description => "Indexes bounded durable interests and expires inactive sanitized snapshots while retaining owner fences.";
    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) => ObservationStoreIndexes.CreateAsync(context, cancellationToken);
}
