using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.ContainerRegistries.Domain;
using Xunit;

namespace Pole.Tests.Integration.ContainerRegistries;

public sealed class ContainerRegistryDomainTests
{
    [Fact]
    public void RegistryProject_UsesExplicitProvisioningStateAndRoundTripsThroughBson()
    {
        var now = new DateTime(2026, 8, 9, 10, 0, 0, DateTimeKind.Utc);
        var project = RegistryProjectFactory.Create(
            "tenant-1",
            ContainerRegistryProvider.Harbor,
            "pole-tenant-1",
            "user-1",
            new RegistryProjectFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                now)) with
        {
            State = new ReadyRegistryProject(
                "42",
                "build-credential",
                "pull-credential",
                now.AddSeconds(1)),
            UpdatedAtUtc = now.AddSeconds(1)
        };

        var roundTripped = BsonSerializer.Deserialize<RegistryProject>(
            project.ToBson());

        var ready = Assert.IsType<ReadyRegistryProject>(roundTripped.State);
        Assert.Equal("build-credential", ready.BuildCredentialId);
        Assert.Equal("pull-credential", ready.PullCredentialId);
    }
}
