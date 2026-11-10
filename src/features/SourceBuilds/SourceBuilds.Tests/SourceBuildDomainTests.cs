using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.SourceBuilds.Domain;
using Xunit;

namespace Pole.Features.SourceBuilds.Tests;

public sealed class SourceBuildDomainTests
{
    [Fact]
    public void BuildJobUsesExplicitStatesAndPreservesImmutableInputThroughBson()
    {
        var now = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var job = Job(now)
            .StartCloning(now.AddSeconds(1))
            .StartAnalyzing(now.AddSeconds(2))
            .StartBuilding(now.AddSeconds(3))
            .StartPushing(
                SourceBuildArtifactFactory.Create("harbor.test/application:build-1", "sha256:digest"),
                now.AddSeconds(4))
            .StartDeploying(
                SourceBuildArtifactFactory.Create("harbor.test/application:build-1", "sha256:digest"),
                "release-1",
                now.AddSeconds(5))
            .Succeed(now.AddSeconds(6));
        var stored = BsonSerializer.Deserialize<SourceBuildJob>(job.ToBson());
        Assert.Equal(job, stored);
        Assert.Equal(new SourceBuildInputVersion(3, 1, 7), stored.InputVersion);
        Assert.Equal(new string('a', 40), stored.CommitSha);
        Assert.Equal("FROM scratch", stored.Recipe.Dockerfile);
        var succeeded = Assert.IsType<SucceededSourceBuild>(stored.State);
        Assert.Equal("release-1", succeeded.ReleaseId);
        Assert.Equal(
            SourceBuildArtifactFactory.Create("harbor.test/application:build-1", "sha256:digest"),
            succeeded.Artifact);
        Assert.Equal("user-1", Assert.IsType<ManualSourceBuildTrigger>(stored.Trigger).UserId);
    }

    [Fact]
    public void BuildStatesPinArtifactAcrossBsonAndRejectDifferentPromotion()
    {
        var now = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var job = Job(now)
            .StartCloning(now)
            .StartAnalyzing(now)
            .StartBuilding(now)
            .StartPushing(SourceBuildArtifactFactory.Create("registry/application:attempt-1", "sha256:original"), now);
        var stored = BsonSerializer.Deserialize<SourceBuildJob>(job.ToBson());
        Assert.Equal(job, stored);
        Assert.Equal(
            SourceBuildArtifactFactory.Create("registry/application:attempt-1", "sha256:original"),
            Assert.IsType<PushingSourceBuild>(stored.State).Artifact);
        var deploying = stored.StartDeploying(
            SourceBuildArtifactFactory.Create("registry/application:attempt-1", "sha256:original"),
            "release-1",
            now);
        var storedDeploying = BsonSerializer.Deserialize<SourceBuildJob>(deploying.ToBson());
        Assert.Equal(deploying, storedDeploying);
        Assert.Equal(
            SourceBuildArtifactFactory.Create("registry/application:attempt-1", "sha256:original"),
            Assert.IsType<DeployingSourceBuild>(storedDeploying.State).Artifact);
        Assert.Throws<ArgumentException>(() => stored.StartDeploying(
            SourceBuildArtifactFactory.Create("registry/application:attempt-1", "sha256:other"),
            "release-1",
            now));
        Assert.Throws<ArgumentException>(() => stored.StartDeploying(
            SourceBuildArtifactFactory.Create("registry/other:attempt-1", "sha256:original"),
            "release-1",
            now));
        Assert.Throws<ArgumentException>(() => SourceBuildPromotionReceiptFactory.Create(
            stored,
            SourceBuildArtifactFactory.Create("registry/application:attempt-1", "sha256:other"),
            "release-1",
            1));
    }

    [Fact]
    public void ArtifactFactoryRequiresNonEmptyReferenceAndDigest()
    {
        Assert.Throws<ArgumentException>(() => SourceBuildArtifactFactory.Create(" ", "sha256:digest"));
        Assert.Throws<ArgumentException>(() => SourceBuildArtifactFactory.Create("registry/application:tag", ""));
    }

    [Fact]
    public void BuildJobRejectsInvalidStateTransition()
    {
        var now = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var exception = Assert.Throws<InvalidOperationException>(() => Job(now).StartBuilding(now));
        Assert.Contains(nameof(QueuedSourceBuild), exception.Message);
        Assert.Contains(nameof(AnalyzingSourceBuild), exception.Message);
    }

    [Fact]
    public void ConfigurationFactoryDefinesInitialRevisionFencesAndDoesNotRequireAnalyzerMetadata()
    {
        var now = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var configuration = SourceBuildConfigurationFactory.Create(
            "tenant-1",
            "api-build",
            "source-1",
            "service-1",
            SourceBuildRecipe.Create("FROM scratch"),
            new ManualSourceBuildConfigurationOrigin("user-1"),
            new SourceBuildConfigurationFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        Assert.Equal(1, configuration.Revision);
        Assert.Equal(0, configuration.ReferenceFence);
        Assert.Equal(0, configuration.BuildGeneration);
        Assert.Equal("api-build", configuration.TechnicalName);
        Assert.Equal(configuration, BsonSerializer.Deserialize<SourceBuildConfiguration>(configuration.ToBson()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Uppercase")]
    [InlineData("1invalid")]
    public void ConfigurationFactoryRejectsInvalidTechnicalName(string name)
    {
        Assert.Throws<ArgumentException>(() => SourceBuildConfigurationFactory.Create(
            "tenant-1",
            name,
            "source-1",
            "service-1",
            new SourceBuildRecipe("FROM scratch"),
            new ManualSourceBuildConfigurationOrigin("user-1"),
            SourceBuildConfigurationFactory.DefaultOptions));
    }

    [Fact]
    public void RecipeRequiresBoundedExecutableDockerfile()
    {
        Assert.Throws<ArgumentException>(() => SourceBuildRecipe.Create("  "));
        Assert.Throws<ArgumentException>(() => SourceBuildRecipe.Create(new string('x', 100_001)));
        Assert.Equal("FROM scratch\n", SourceBuildRecipe.Create("FROM scratch\n").Dockerfile);
    }

    [Fact]
    public void PromotionReceiptRetainsExactJobGenerationAndRoundTrips()
    {
        var now = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        var job = Job(now)
            .StartCloning(now)
            .StartAnalyzing(now)
            .StartBuilding(now)
            .StartPushing(SourceBuildArtifactFactory.Create("registry/application:build-1", "sha256:digest"), now);
        var receipt = SourceBuildPromotionReceiptFactory.Create(
            job,
            SourceBuildArtifactFactory.Create("registry/application:build-1", "sha256:digest"),
            "release-1",
            2);
        Assert.Equal(job.Id, receipt.Id);
        Assert.Equal(job.SourceRepositoryId, receipt.SourceRepositoryId);
        Assert.Equal(job.CommitSha, receipt.CommitSha);
        Assert.Equal(
            SourceBuildArtifactFactory.Create("registry/application:build-1", "sha256:digest"),
            receipt.Artifact);
        Assert.Equal(job.InputVersion.Generation, receipt.Generation);
        Assert.Equal(job.InputVersion.ConfigurationRevision, receipt.ConfigurationRevision);
        Assert.Equal(receipt, BsonSerializer.Deserialize<SourceBuildPromotionReceipt>(receipt.ToBson()));
    }

    private static SourceBuildJob Job(DateTime now) => SourceBuildJobFactory.CreateManual(
        "tenant-1",
        "configuration-1",
        "source-1",
        "service-1",
        "main",
        new string('a', 40),
        new SourceBuildInputVersion(3, 1, 7),
        new SourceBuildRecipe("FROM scratch"),
        "user-1",
        "user@example.com",
        new SourceBuildJobFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
}
