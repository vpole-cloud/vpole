using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application.HostnameBindings;
using Pole.Features.Domains.Application.Lifecycle;
using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Domain;
using Xunit;

namespace Pole.Tests.Integration.Domains;

public sealed class DomainMongoCollectionRegistrationTests
{
    [Fact]
    public void Domain_persistence_registers_the_feature_owned_collection_names()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoDatabase>(new MongoClient("mongodb://localhost").GetDatabase("pole-test"));
        services.AddDomainPersistence();

        using var provider = services.BuildServiceProvider();
        Assert.Equal("TenantDomainZones",
            provider.GetRequiredService<IMongoCollection<TenantDomainZone>>().CollectionNamespace.CollectionName);
        Assert.Equal("HostnameBindings",
            provider.GetRequiredService<IMongoCollection<HostnameBinding>>().CollectionNamespace.CollectionName);
    }
}
