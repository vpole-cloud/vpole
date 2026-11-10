using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class IntegrationFixtureContractTests
{
    [Fact]
    public void EveryMongoTestDeclaresItsCollectionAndPhysicalIsolationWhenGlobal()
    {
        var types = typeof(IntegrationFixtureContractTests).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsClass: true } && type.GetConstructors()
                .Any(ctor =>
                    ctor.GetParameters().Any(parameter => parameter.ParameterType == typeof(MongoReplicaSetFixture))));
        Assert.NotEmpty(types);
        foreach (var type in types)
        {
            var collection = Assert.Single(
                type.CustomAttributes,
                attribute => attribute.AttributeType == typeof(CollectionAttribute));
            var name = (string)Assert.Single(collection.ConstructorArguments).Value!;
            Assert.Contains(
                name,
                new[]
                {
                    IntegrationTestCollection.Global, IntegrationTestCollection.Global2,
                    IntegrationTestCollection.Global3, IntegrationTestCollection.Global4,
                    IntegrationTestCollection.Exclusive, IntegrationTestCollection.Tenant1,
                    IntegrationTestCollection.Tenant2, IntegrationTestCollection.Tenant3,
                    IntegrationTestCollection.Tenant4,
                });
            if (IntegrationTestCollection.IsGlobal(name))
                Assert.True(typeof(IIsolatedIntegrationTest).IsAssignableFrom(type), type.FullName);
        }
    }
}
