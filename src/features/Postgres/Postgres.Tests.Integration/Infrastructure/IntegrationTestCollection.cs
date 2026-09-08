using Pole.Testing;
using Xunit;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

[CollectionDefinition(Name)]
public sealed class IntegrationTestCollection : ICollectionFixture<MongoReplicaSetFixture>
{
    public const string Name = "postgres-integration-tests";
}
