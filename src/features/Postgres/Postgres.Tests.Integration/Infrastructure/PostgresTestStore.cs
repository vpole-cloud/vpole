using Pole.Features.Outbox.Domain;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.StorageProfiles.Domain;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

public sealed class PostgresTestStore(
    IMongoClient client,
    IMongoDatabase database
)
{
    public IMongoClient Client { get; } = client;
    public IMongoDatabase Database { get; } = database;
    public IMongoCollection<PostgresCluster> Postgres { get; } = database.GetCollection<PostgresCluster>("Postgres");

    public IMongoCollection<ResourceGroup> ResourceGroups { get; }
        = database.GetCollection<ResourceGroup>("ResourceGroups");

    public IMongoCollection<IsolationScope> IsolationScopes { get; }
        = database.GetCollection<IsolationScope>("IsolationScopes");

    public IMongoCollection<RuntimeCell> RuntimeCells { get; } = database.GetCollection<RuntimeCell>("RuntimeCells");

    public IMongoCollection<ResourceRegistration> ResourceRegistrations { get; }
        = database.GetCollection<ResourceRegistration>("ResourceRegistrations");

    public IMongoCollection<ResourceDeployment> ResourceDeployments { get; }
        = database.GetCollection<ResourceDeployment>("ResourceDeployments");

    public IMongoCollection<StorageProfile> StorageProfiles { get; }
        = database.GetCollection<StorageProfile>("StorageProfiles");

    public IMongoCollection<OutboxMessage> OutboxMessages { get; }
        = database.GetCollection<OutboxMessage>("OutboxMessages");

    public IMongoCollection<Tenant> Tenants { get; } = database.GetCollection<Tenant>("Tenants");
}
