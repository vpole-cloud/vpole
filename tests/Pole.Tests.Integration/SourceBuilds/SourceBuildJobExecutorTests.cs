using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Application.Jobs;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Events;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

public sealed class SourceBuildJobExecutorTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Executor_ClaimsOnceBuildsExactCommitAndWaitsForDeploymentCompletion()
    {
        var now = DateTime.UtcNow;
        var recipe = new SourceBuildRecipe(
            "candidate",
            SourceBuildRecipeKind.DotNet,
            ".",
            "App.csproj",
            ImmutableList.Create("dotnet publish"),
            "dotnet App.dll",
            "FROM scratch",
            false);
        var sourceRepositoryId = ObjectId.GenerateNewId().ToString();
        var configuration = SourceBuildConfigurationFactory.Create(
            TestData.TenantId,
            sourceRepositoryId,
            ObjectId.GenerateNewId().ToString(),
            recipe with { Dockerfile = "FROM changed-after-enqueue" },
            TestData.UserId,
            new SourceBuildConfigurationFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        var jobRecipe = recipe with { Dockerfile = "FROM snapshot" };
        var job = SourceBuildJobFactory.CreateManual(
            TestData.TenantId,
            configuration.Id,
            sourceRepositoryId,
            configuration.WebServiceId,
            "main",
            "exact-commit",
            jobRecipe,
            TestData.UserId,
            TestData.UserEmail,
            new SourceBuildJobFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        await Fixture.DbContext.SourceBuildConfigurations.InsertOneAsync(configuration);
        await Fixture.DbContext.SourceBuildJobs.InsertOneAsync(job);

        var workspace = new FakeWorkspaceService();
        var buildKit = new FakeBuildKitClient();
        var deployment = new FakeDeploymentService();
        var executor = new SourceBuildJobExecutor(
            Fixture.DbContext,
            workspace,
            new FakeRegistryBuildService(),
            buildKit,
            deployment,
            TimeProvider.System,
            NullLogger<SourceBuildJobExecutor>.Instance);

        await executor.ExecuteAsync(job.Id);
        await executor.ExecuteAsync(job.Id);

        var deployingJob = await Fixture.DbContext.SourceBuildJobs.Find(item => item.Id == job.Id).SingleAsync();
        var deploying = Assert.IsType<DeployingSourceBuild>(deployingJob.State);
        Assert.Equal("release-1", deploying.ReleaseId);
        Assert.Equal("exact-commit", workspace.DownloadedCommit);
        Assert.Equal(TestData.TenantId, workspace.TenantId);
        Assert.Equal(sourceRepositoryId, workspace.SourceRepositoryId);
        Assert.Equal("FROM snapshot", buildKit.Request!.Dockerfile);
        Assert.Equal(1, buildKit.Calls);
        Assert.Equal(Digest, deployment.Request!.Image.Digest);
        Assert.Equal("pole-registry-507f1f77bcf86cd799439012", deployment.Request.Image.PullSecretName);

        var completion = new CompleteSourceBuildOnDeploymentConsumer(Fixture.DbContext, TimeProvider.System);
        await completion.HandleAsync(new WebServiceDeploymentCompletedEvent(
            "release-1",
            job.WebServiceId,
            true,
            null)
        {
            EventCore = Pole.Features.Events.Abstractions.EventCore.Create(TestData.TenantId)
        });

        var succeededJob = await Fixture.DbContext.SourceBuildJobs.Find(item => item.Id == job.Id).SingleAsync();
        Assert.IsType<SucceededSourceBuild>(succeededJob.State);
    }

    private sealed class FakeWorkspaceService : ISourceBuildWorkspaceService
    {
        public string? TenantId { get; private set; }
        public string? SourceRepositoryId { get; private set; }
        public string? DownloadedCommit { get; private set; }

        public async Task<Result<SourceBuildWorkspace>> CreateAsync(
            string tenantId,
            string sourceRepositoryId,
            string commitSha,
            string buildJobId,
            CancellationToken cancellationToken = default)
        {
            TenantId = tenantId;
            SourceRepositoryId = sourceRepositoryId;
            DownloadedCommit = commitSha;
            var root = Path.Combine(Path.GetTempPath(), $"pole-executor-test-{Guid.NewGuid():N}");
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "App.csproj"), "<Project />", cancellationToken);
            return new SourceBuildWorkspace(root, source);
        }
    }

    private sealed class FakeRegistryBuildService : IContainerRegistryBuildService
    {
        public Task<Result<RegistryBuildAccess>> PreparePushAsync(
            string tenantId,
            string initiatedByUserId,
            string repositoryName,
            string tag,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<RegistryBuildAccess>>(new RegistryBuildAccess(
                "507f1f77bcf86cd799439012",
                new RegistryPushTarget("harbor.test", "pole-test", repositoryName, tag),
                "pole-registry-507f1f77bcf86cd799439012",
                "robot$build",
                "build-secret"));

        public Task<Result<RegistryArtifact>> GetArtifactAsync(
            string tenantId,
            string registryProjectId,
            string repositoryName,
            string reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<RegistryArtifact>>(
                new RegistryArtifact(repositoryName, reference, Digest, 100));
    }

    private sealed class FakeBuildKitClient : IBuildKitClient
    {
        public int Calls { get; private set; }
        public BuildKitBuildRequest? Request { get; private set; }

        public Task<Result<BuiltContainerImage>> BuildAndPushAsync(
            BuildKitBuildRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            return Task.FromResult<Result<BuiltContainerImage>>(
                new BuiltContainerImage(request.Target.Reference, Digest));
        }
    }

    private sealed class FakeDeploymentService : IWebServiceSourceBuildDeploymentService
    {
        public SourceBuildDeploymentRequest? Request { get; private set; }

        public Task<Result<SourceBuildDeploymentResult>> DeployAsync(
            SourceBuildDeploymentRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult<Result<SourceBuildDeploymentResult>>(
                new SourceBuildDeploymentResult("release-1", 2));
        }
    }
}
