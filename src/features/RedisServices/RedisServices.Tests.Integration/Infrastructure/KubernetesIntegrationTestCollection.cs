using Xunit;

namespace Pole.Features.RedisServices.Tests.Integration.Infrastructure;

[CollectionDefinition(Name)]
public sealed class KubernetesIntegrationTestCollection : ICollectionFixture<KubernetesClusterFixture>
{
    public const string Name = "kubernetes-integration-tests";
}
