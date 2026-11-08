using Pole.Testing;
using Xunit;

namespace Pole.Tests.Kubernetes;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KubernetesLabCollection : ICollectionFixture<KubernetesClusterFixture>
{
    public const string Name = KubernetesIntegrationTestBase.CollectionName;
}
