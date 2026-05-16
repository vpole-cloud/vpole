using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

[CollectionDefinition(Name)]
public sealed class KeycloakIntegrationTestCollection : ICollectionFixture<KeycloakContainerFixture>
{
    public const string Name = "keycloak-integration-tests";
}
