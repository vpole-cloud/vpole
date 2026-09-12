using Pole.Testing;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

[CollectionDefinition(Name)]
public sealed class KubernetesIntegrationTestCollection : ICollectionFixture<KubernetesClusterFixture>
{
    public const string Name = KubernetesIntegrationTestBase.CollectionName;
}
