using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Pole.Features.Events.Abstractions;
using Pole.Features.Postgres.Application.Search;
using Pole.Features.Postgres.Events;
using Pole.Features.Postgres.Tests.Integration.Infrastructure;
using Pole.Features.Search.Domain;
using Xunit;

namespace Pole.Features.Postgres.Tests.Integration;

public sealed class PostgresSearchTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task CreatedEventProjectsThePostgresSource()
    {
        var postgresId = ObjectId.GenerateNewId().ToString();
        var resourceGroupId = ObjectId.GenerateNewId().ToString();
        var CreateProjection = Services.GetRequiredService<CreateSearchProjection>();
        await new AddPostgresClusterToSearchOnPostgresClusterCreatedConsumer(CreateProjection).Handle(
            new PostgresClusterCreatedEvent(
                EventCore.Create(PostgresTestData.TenantId),
                postgresId,
                "database",
                resourceGroupId,
                1,
                new PostgresClusterSpecSnapshot(1, 10, PostgresTestData.DefaultStorageProfileId, 17)),
            CancellationToken.None);

        var sources = await Fixture.Database
            .GetCollection<SearchSourceDocument>("SearchSourceDocuments")
            .Find(source => source.TenantId == PostgresTestData.TenantId)
            .ToListAsync();
        Assert.Equal("PostgresCluster", Assert.Single(sources).SourceApiName);
        Assert.Equal("pole/postgresql", sources[0].ResourceTypeId);
    }
}
