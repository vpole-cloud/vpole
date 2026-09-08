using Pole.Testing;
using Xunit;

namespace Pole.Features.RedisServices.Tests.Integration.Infrastructure;

[CollectionDefinition(Name)]
public sealed class IntegrationTestCollection : ICollectionFixture<MongoReplicaSetFixture>
{
    public const string Name = "redis-integration-tests";
}
