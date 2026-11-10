using Pole.Features.Outbox.Domain;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.StorageProfiles.Domain;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.RedisServices.Tests.Integration.Infrastructure;

public sealed class RedisTestStore(
    IMongoClient client,
    IMongoDatabase database
)
{
    public IMongoClient Client { get; } = client;
    public IMongoDatabase Database { get; } = database;
    public IMongoCollection<Redis> RedisServices { get; } = database.GetCollection<Redis>("RedisServices");

    public IMongoCollection<ResourceGroup> ResourceGroups { get; }
        = database.GetCollection<ResourceGroup>("ResourceGroups");

    public IMongoCollection<IsolationScope> IsolationScopes { get; }
        = database.GetCollection<IsolationScope>("IsolationScopes");

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
