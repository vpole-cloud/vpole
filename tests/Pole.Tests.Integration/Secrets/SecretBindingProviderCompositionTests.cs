using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

[Collection(IntegrationTestCollection.Tenant4)]
public sealed class SecretBindingProviderCompositionTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public void Provider_composes_and_validates_a_registered_consumer_capability()
    {
        var provider = Services.GetServices<IDeclarativeResourceProvider>()
            .Single(item => item.Kind == "SecretBinding");
        using var spec = JsonDocument.Parse(
            """
            {
              "secret": "credentials",
              "consumer": { "kind": "PostgresCluster", "name": "database" },
              "usage": "Bootstrap",
              "keyMappings": {}
            }
            """);

        var normalized = provider.NormalizeAndValidate(spec.RootElement);

        Assert.Equal("SecretBinding", provider.Kind);
        Assert.Equal("PostgresCluster", normalized.GetProperty("consumer").GetProperty("kind").GetString());
        Assert.Equal("Bootstrap", normalized.GetProperty("usage").GetString());
    }
}
