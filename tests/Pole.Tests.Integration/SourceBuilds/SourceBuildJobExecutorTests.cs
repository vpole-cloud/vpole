using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Provisioning;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.Features.GitProviders.Domain;
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
        var connection = GitProviderConnectionFactory.Create(
            TestData.TenantId,
            GitProviderKind.GitHub,
            "42",
            "account",
            "lsbV",
            GitProviderAccountType.User,
            GitProviderRepositorySelection.Selected,
            TestData.UserId,
            new GitProviderConnectionFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(), now, now));
        var plan = new SourceBuildPlan(
            recipe.CandidateKey,
            SourceBuildPlanKind.DotNet,
            "App",
            ".",
            recipe.ManifestPath,
            SourceBuildPlanConfidence.High,
            ImmutableList<string>.Empty,
            ImmutableList<string>.Empty,
            recipe.Commands,
            recipe.StartCommand,
            recipe.Dockerfile);
        var source = SourceRepositoryFactory.Create(
            TestData.TenantId,
            connection.Id,
            "9001",
            "lsbV/sample",
            "https://github.com/lsbV/sample",
            true,
            new SourceRepositoryAnalysis(
                "main", "newer-commit", now, ImmutableList.Create("App.csproj"), ImmutableList.Create(plan)),
            plan,
            new SourceRepositoryFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now, now));
        var configuration = SourceBuildConfigurationFactory.Create(
            TestData.TenantId,
            source.Id,
            ObjectId.GenerateNewId().ToString(),
            recipe with { Dockerfile = "FROM changed-after-enqueue" },
            TestData.UserId,
            new SourceBuildConfigurationFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        var jobRecipe = recipe with { Dockerfile = "FROM snapshot" };
        var job = SourceBuildJobFactory.CreateManual(
            TestData.TenantId,
            configuration.Id,
            source.Id,
            configuration.WebServiceId,
            "main",
            "exact-commit",
            jobRecipe,
            TestData.UserId,
            TestData.UserEmail,
            new SourceBuildJobFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        await Fixture.DbContext.GitProviderConnections.InsertOneAsync(connection);
        await Fixture.DbContext.SourceRepositories.InsertOneAsync(source);
        await Fixture.DbContext.SourceBuildConfigurations.InsertOneAsync(configuration);
        await Fixture.DbContext.SourceBuildJobs.InsertOneAsync(job);

        var workspace = new FakeWorkspaceService();
        var buildKit = new FakeBuildKitClient();
        var deployment = new FakeDeploymentService();
        var executor = new SourceBuildJobExecutor(
            Fixture.DbContext,
            Fixture.DbContext,
            workspace,
            new FakeProvisioningService(CreateReadyRegistryProject()),
            new FakeCredentialResolver(),
            new FakeRegistryClient(),
            buildKit,
            deployment,
            new HarborOptions
            {
                Enabled = true,
                BaseUrl = "https://harbor.test",
                RegistryHost = "harbor.test",
                Username = "admin",
                Password = "admin-secret"
            },
            TimeProvider.System,
            NullLogger<SourceBuildJobExecutor>.Instance);

        await executor.ExecuteAsync(job.Id);
        await executor.ExecuteAsync(job.Id);

        var deployingJob = await Fixture.DbContext.SourceBuildJobs.Find(item => item.Id == job.Id).SingleAsync();
        var deploying = Assert.IsType<DeployingSourceBuild>(deployingJob.State);
        Assert.Equal("release-1", deploying.ReleaseId);
        Assert.Equal("exact-commit", workspace.DownloadedCommit);
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

    private static RegistryProject CreateReadyRegistryProject()
    {
        var now = DateTime.UtcNow;
        return RegistryProjectFactory.Create(
            TestData.TenantId,
            ContainerRegistryProvider.Harbor,
            "pole-test",
            TestData.UserId,
            new RegistryProjectFactory.CreateOptions("507f1f77bcf86cd799439012", now)) with
        {
            State = new ReadyRegistryProject("17", "build-credential", "pull-credential", now)
        };
    }

    private sealed class FakeWorkspaceService : ISourceBuildWorkspaceService
    {
        public string? DownloadedCommit { get; private set; }

        public async Task<Result<SourceBuildWorkspace>> CreateAsync(
            GitProviderConnection connection,
            SourceRepository sourceRepository,
            string buildJobId,
            CancellationToken cancellationToken = default)
        {
            DownloadedCommit = sourceRepository.CommitSha;
            var root = Path.Combine(Path.GetTempPath(), $"pole-executor-test-{Guid.NewGuid():N}");
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "App.csproj"), "<Project />", cancellationToken);
            return new SourceBuildWorkspace(root, source);
        }
    }

    private sealed class FakeProvisioningService(RegistryProject project)
        : IContainerRegistryProvisioningService
    {
        public Task<Result<RegistryProject>> EnsureTenantProjectAsync(
            string tenantId,
            string initiatedByUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<RegistryProject>>(project);
    }

    private sealed class FakeCredentialResolver : IRegistryCredentialResolver
    {
        public Task<Result<ResolvedRegistryCredential>> ResolveAsync(
            string tenantId,
            string registryProjectId,
            RegistryCredentialPurpose purpose,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<ResolvedRegistryCredential>>(
                new ResolvedRegistryCredential("robot$build", "build-secret", null));
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

    private sealed class FakeRegistryClient : IContainerRegistryClient
    {
        public Task<Result<ProvisionedRegistryProject>> ProvisionProjectAsync(
            RegistryProjectProvisioningRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<RegistryArtifact>> GetArtifactAsync(
            string projectName,
            string repositoryName,
            string reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<RegistryArtifact>>(
                new RegistryArtifact(repositoryName, reference, Digest, 100));

        public Task<Result<RegistryRobotCleanupResult>> DeleteRobotCredentialsAsync(
            IReadOnlyCollection<RegistryRobotCleanupTarget> targets,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> DeleteProjectAsync(
            string externalProjectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
