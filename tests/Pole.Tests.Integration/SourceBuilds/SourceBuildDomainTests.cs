using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.ContainerRegistries.Domain;
using Pole.Features.SourceBuilds.Domain;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

public sealed class SourceBuildDomainTests
{
    [Fact]
    public void BuildJob_UsesExplicitStatesAndRoundTripsThroughBson()
    {
        var startedAtUtc = new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc);
        var job = SourceBuildJobFactory.CreateManual(
                "tenant-1",
                "configuration-1",
                "source-1",
                "service-1",
                "main",
                "abc1234",
                TestRecipe(),
                "user-1",
                "user@example.com",
                new SourceBuildJobFactory.CreateOptions(
                    ObjectId.GenerateNewId().ToString(),
                    startedAtUtc))
            .StartCloning(startedAtUtc.AddSeconds(1))
            .StartAnalyzing(startedAtUtc.AddSeconds(2))
            .StartBuilding(startedAtUtc.AddSeconds(3))
            .StartPushing("harbor.test/pole-tenant-1/service-1:abc1234", startedAtUtc.AddSeconds(4))
            .StartDeploying("sha256:digest", "release-1", startedAtUtc.AddSeconds(5))
            .Succeed(startedAtUtc.AddSeconds(6));

        var roundTripped = BsonSerializer.Deserialize<SourceBuildJob>(job.ToBson());

        var succeeded = Assert.IsType<SucceededSourceBuild>(roundTripped.State);
        Assert.Equal("sha256:digest", succeeded.ImageDigest);
        Assert.Equal("release-1", succeeded.ReleaseId);
        var trigger = Assert.IsType<ManualSourceBuildTrigger>(roundTripped.Trigger);
        Assert.Equal("user-1", trigger.UserId);
    }

    [Fact]
    public void BuildJob_RejectsInvalidStateTransition()
    {
        var now = DateTime.UtcNow;
        var job = SourceBuildJobFactory.CreateManual(
            "tenant-1",
            "configuration-1",
            "source-1",
            "service-1",
            "main",
            "abc1234",
            TestRecipe(),
            "user-1",
            null,
            new SourceBuildJobFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));

        var exception = Assert.Throws<InvalidOperationException>(() => job.StartBuilding(now.AddSeconds(1)));

        Assert.Contains(nameof(QueuedSourceBuild), exception.Message);
        Assert.Contains(nameof(AnalyzingSourceBuild), exception.Message);
    }

    [Fact]
    public void RegistryProject_UsesExplicitProvisioningStateAndRoundTripsThroughBson()
    {
        var now = DateTime.UtcNow;
        var project = RegistryProjectFactory.Create(
            "tenant-1",
            ContainerRegistryProvider.Harbor,
            "pole-tenant-1",
            "user-1",
            new RegistryProjectFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now)) with
        {
            State = new ReadyRegistryProject("42", "build-credential", "pull-credential", now.AddSeconds(1)),
            UpdatedAtUtc = now.AddSeconds(1)
        };

        var roundTripped = BsonSerializer.Deserialize<RegistryProject>(project.ToBson());

        var ready = Assert.IsType<ReadyRegistryProject>(roundTripped.State);
        Assert.Equal("build-credential", ready.BuildCredentialId);
        Assert.Equal("pull-credential", ready.PullCredentialId);
    }

    [Fact]
    public void BuildConfiguration_OwnsAnImmutableRecipeSnapshot()
    {
        var now = DateTime.UtcNow;
        var recipe = new SourceBuildRecipe(
            "DotNet:src/App/App.csproj",
            SourceBuildRecipeKind.DotNet,
            "src/App",
            "src/App/App.csproj",
            ImmutableList.Create("dotnet restore", "dotnet publish"),
            "dotnet App.dll",
            "FROM mcr.microsoft.com/dotnet/aspnet:10.0",
            IsCustomized: false);

        var configuration = SourceBuildConfigurationFactory.Create(
            "tenant-1",
            "source-1",
            "service-1",
            recipe,
            "user-1",
            new SourceBuildConfigurationFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                now));

        Assert.Same(recipe, configuration.Recipe);
        Assert.False(configuration.Recipe.IsCustomized);
    }

    private static SourceBuildRecipe TestRecipe() =>
        new(
            "DotNet:src/App/App.csproj",
            SourceBuildRecipeKind.DotNet,
            "src/App",
            "src/App/App.csproj",
            ImmutableList.Create("dotnet publish"),
            "dotnet App.dll",
            "FROM scratch",
            IsCustomized: false);
}
