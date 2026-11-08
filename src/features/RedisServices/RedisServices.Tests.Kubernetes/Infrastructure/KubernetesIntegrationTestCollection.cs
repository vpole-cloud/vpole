using Pole.Testing;
using Xunit;

namespace Pole.Features.RedisServices.Tests.Kubernetes.Infrastructure;

[CollectionDefinition(Name)]
public sealed class KubernetesIntegrationTestCollection : ICollectionFixture<KubernetesClusterFixture>
{
    public const string Name = KubernetesIntegrationTestBase.CollectionName;
}
