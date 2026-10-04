using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.SourceBuilds.Application.Jobs;
using Pole.Features.SourceBuilds.Application.Persistence;
using Pole.Features.SourceBuilds.Application.Persistence.Jobs;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Application.Consumers;
using Pole.Features.WebServices.Events;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

public sealed class SourceBuildJobExecutorTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private ServiceProvider? captureServices;

    [Fact]
    public async Task Unexpected_build_exception_does_not_emit_known_registry_inputs_in_logs_or_failure()
    {
        var queued = await QueueJobAsync();
        var build = new FakeBuildKitClient { ThrowUnexpected = true };
        var logger = new RecordingLogger();
        await CreateExecutor(new FakeWorkspaceService(), new FakeRegistryBuildService(), build, new FakeDeploymentService(), logger).ExecuteAsync(queued.Job.Id);
        Assert.NotNull(build.Request);
        Assert.DoesNotContain(build.Request.RegistrySecret, string.Join("\n", logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain(build.Request.RegistryUsername, string.Join("\n", logger.Messages), StringComparison.Ordinal);
        var failed = Assert.IsType<FailedSourceBuild>((await Fixture.DbContext.SourceBuildJobs.Find(x => x.Id == queued.Job.Id).SingleAsync()).State);
        Assert.Equal("UnexpectedBuildFailure", failed.Code);
        Assert.DoesNotContain(build.Request.RegistrySecret, failed.Message, StringComparison.Ordinal);
    }

    public override async Task DisposeAsync()
    {
        if (captureServices is not null) await captureServices.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task<SourceBuildLogCapture> CreateCaptureAsync(SourceBuildJob job)
    {
        captureServices ??= new ServiceCollection().AddLogging().AddSingleton(Fixture.Database)
            .AddSourceBuildsFeature(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).BuildServiceProvider();
        return captureServices.GetRequiredService<CreateSourceBuildLogCapture>()(job);
    }

    [Fact]
    public async Task Executor_ClaimsOnceBuildsExactCommitAndWaitsForDeploymentCompletion()
    {
        var queued = await QueueJobAsync();
        var workspace = new FakeWorkspaceService();
        var buildKit = new FakeBuildKitClient();
        var deployment = new FakeDeploymentService();
        var executor = CreateExecutor(workspace, new FakeRegistryBuildService(), buildKit, deployment);

        await executor.ExecuteAsync(queued.Job.Id);
        await executor.ExecuteAsync(queued.Job.Id);

        var deployingJob = await Fixture.DbContext.SourceBuildJobs.Find(item => item.Id == queued.Job.Id)
            .SingleAsync();
        var deploying = Assert.IsType<DeployingSourceBuild>(deployingJob.State);
        Assert.Equal("release-1", deploying.ReleaseId);
        Assert.Equal("exact-commit", workspace.DownloadedCommit);
        Assert.Equal(TestData.TenantId, workspace.TenantId);
        Assert.Equal(queued.SourceRepositoryId, workspace.SourceRepositoryId);
        Assert.Equal("FROM snapshot", buildKit.Request!.Dockerfile);
        Assert.Equal(1, buildKit.Calls);
        Assert.Equal(Digest, deployment.Request!.Image.Digest);
        Assert.Equal("pole-registry-507f1f77bcf86cd799439012", deployment.Request.Image.PullSecretName);

        var completion = new CompleteSourceBuildOnDeploymentConsumer(
            CompleteDeploymentAsync,
            TimeProvider.System);
        await completion.HandleAsync(new WebServiceDeploymentCompletedEvent(
            "release-1",
            queued.Job.WebServiceId,
            "507f1f77bcf86cd799439071",
            ResourceRevision: 2,
            DeploymentGeneration: 3,
            Operation: "Apply",
            Succeeded: true,
            Retryable: false,
            FailureCode: null,
            FailureMessage: null,
            ReleaseOutcome: "Pending",
            ReleaseTerminal: false)
        {
            EventCore = Pole.Features.Events.Abstractions.EventCore.Create(TestData.TenantId)
        });

        var stillDeployingJob = await Fixture.DbContext.SourceBuildJobs
            .Find(item => item.Id == queued.Job.Id)
            .SingleAsync();
        Assert.IsType<DeployingSourceBuild>(stillDeployingJob.State);

        await completion.HandleAsync(new WebServiceDeploymentCompletedEvent(
            "release-1",
            queued.Job.WebServiceId,
            "507f1f77bcf86cd799439072",
            ResourceRevision: 2,
            DeploymentGeneration: 3,
            Operation: "Apply",
            Succeeded: true,
            Retryable: false,
            FailureCode: null,
            FailureMessage: null,
            ReleaseOutcome: "Succeeded",
            ReleaseTerminal: true)
        {
            EventCore = Pole.Features.Events.Abstractions.EventCore.Create(TestData.TenantId)
        });

        var succeededJob = await Fixture.DbContext.SourceBuildJobs.Find(item => item.Id == queued.Job.Id)
            .SingleAsync();
        Assert.IsType<SucceededSourceBuild>(succeededJob.State);
    }

    [Fact]
    public async Task Executor_MapsTypedDeploymentRejectionToStableBuildFailure()
    {
        var queued = await QueueJobAsync();
        var deployment = new FakeDeploymentService(
            new SourceBuildWebServiceDeploymentError(
                new SourceBuildWebServiceDeploymentRejected(
                    queued.Job.WebServiceId,
                    new SourceBuildResourceDeploymentRejection(
                        new SourceBuildResourceDeploymentPlacementUnavailable("deployment-1")))));
        var executor = CreateExecutor(
            new FakeWorkspaceService(),
            new FakeRegistryBuildService(),
            new FakeBuildKitClient(),
            deployment);

        await executor.ExecuteAsync(queued.Job.Id);

        var failedJob = await Fixture.DbContext.SourceBuildJobs
            .Find(item => item.Id == queued.Job.Id)
            .SingleAsync();
        var failed = Assert.IsType<FailedSourceBuild>(failedJob.State);
        Assert.Equal("ResourceDeploymentPlacementUnavailable", failed.Code);
        Assert.Contains("SourceBuildResourceDeploymentPlacementUnavailable", failed.Message);
    }

    private SourceBuildJobExecutor CreateExecutor(
        FakeWorkspaceService workspace,
        FakeRegistryBuildService registry,
        FakeBuildKitClient buildKit,
        FakeDeploymentService deployment,
        ILogger<SourceBuildJobExecutor>? logger = null) =>
        new(
            ClaimAsync,
            LoadConfigurationAsync,
            LoadForExecutionAsync,
            workspace.InvokeAsync,
            registry.PreparePushAsync,
            buildKit.InvokeAsync,
            registry.GetArtifactAsync,
            deployment.DeployAsync,
            TryPersistAsync,
            TimeProvider.System,
            logger ?? NullLogger<SourceBuildJobExecutor>.Instance,
            CreateCaptureAsync);

    private async Task<SourceBuildJob?> ClaimAsync(
        string buildJobId,
        DateTime claimedAtUtc,
        CancellationToken cancellationToken)
    {
        var claimed = await Fixture.DbContext.SourceBuildJobs.FindOneAndUpdateAsync(
            Builders<SourceBuildJob>.Filter.And(
                Builders<SourceBuildJob>.Filter.Eq(item => item.Id, buildJobId),
                Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, QueuedSourceBuild>(
                    item => item.State)),
            Builders<SourceBuildJob>.Update
                .Set(item => item.State, new CloningSourceBuild(claimedAtUtc))
                .Set(item => item.UpdatedAtUtc, claimedAtUtc),
            new FindOneAndUpdateOptions<SourceBuildJob> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        return claimed;
    }

    private async Task<SourceBuildConfiguration?> LoadConfigurationAsync(
        string tenantId,
        string configurationId,
        CancellationToken cancellationToken) =>
        await Fixture.DbContext.SourceBuildConfigurations
            .Find(item => item.TenantId == tenantId && item.Id == configurationId)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<SourceBuildJob?> LoadForExecutionAsync(
        string buildJobId,
        CancellationToken cancellationToken) =>
        await Fixture.DbContext.SourceBuildJobs
            .Find(item => item.Id == buildJobId)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<bool> TryPersistAsync(
        SourceBuildJob current,
        SourceBuildJob next,
        CancellationToken cancellationToken)
    {
        var updated = await Fixture.DbContext.SourceBuildJobs.ReplaceOneAsync(
            item => item.Id == current.Id && item.TenantId == current.TenantId &&
                    item.UpdatedAtUtc == current.UpdatedAtUtc,
            next,
            cancellationToken: cancellationToken);
        return updated.ModifiedCount == 1;
    }

    private async Task CompleteDeploymentAsync(
        SourceBuildDeploymentCompletion completion,
        CancellationToken cancellationToken)
    {
        var job = await Fixture.DbContext.SourceBuildJobs.Find(item =>
                item.TenantId == completion.TenantId &&
                item.State is DeployingSourceBuild &&
                ((DeployingSourceBuild)item.State).ReleaseId == completion.ReleaseId)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
            return;

        var terminal = completion.ReleaseOutcome == "Succeeded"
            ? job.Succeed(completion.CompletedAtUtc)
            : job.Fail(
                SourceBuildPhase.Deploying,
                "WebServiceDeploymentFailed",
                completion.FailureMessage ?? "Web service deployment failed.",
                completion.CompletedAtUtc);
        await TryPersistAsync(job, terminal, cancellationToken);
    }

    private async Task<QueuedJob> QueueJobAsync()
    {
        var now = DateTime.UtcNow;
        var recipe = new SourceBuildRecipe(
            "candidate",
            SourceBuildRecipeKind.DotNet,
            ".",
            "App.csproj",
            ImmutableList.Create("dotnet publish"),
            "dotnet App.dll",
            "FROM snapshot",
            false);
        var sourceRepositoryId = ObjectId.GenerateNewId().ToString();
        var configuration = SourceBuildConfigurationFactory.Create(
            TestData.TenantId,
            sourceRepositoryId,
            ObjectId.GenerateNewId().ToString(),
            recipe with { Dockerfile = "FROM changed-after-enqueue" },
            TestData.UserId,
            new SourceBuildConfigurationFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        var job = SourceBuildJobFactory.CreateManual(
            TestData.TenantId,
            configuration.Id,
            sourceRepositoryId,
            configuration.WebServiceId,
            "main",
            "exact-commit",
            recipe,
            TestData.UserId,
            TestData.UserEmail,
            new SourceBuildJobFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        await Fixture.DbContext.SourceBuildConfigurations.InsertOneAsync(configuration);
        await Fixture.DbContext.SourceBuildJobs.InsertOneAsync(job);
        return new QueuedJob(job, sourceRepositoryId);
    }

    private sealed record QueuedJob(
        SourceBuildJob Job,
        string SourceRepositoryId);

    private sealed class FakeWorkspaceService
    {
        public string? TenantId { get; private set; }
        public string? SourceRepositoryId { get; private set; }
        public string? DownloadedCommit { get; private set; }

        public async Task<CreateSourceBuildWorkspaceOutcome> InvokeAsync(
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
            await File.WriteAllTextAsync(
                Path.Combine(source, "App.csproj"),
                "<Project />",
                cancellationToken);
            return new SourceBuildWorkspace(root, source);
        }
    }

    private sealed class FakeRegistryBuildService
    {
        public Task<PrepareRegistryBuildPushOutcome> PreparePushAsync(
            string tenantId,
            string initiatedByUserId,
            string repositoryName,
            string tag,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PrepareRegistryBuildPushOutcome>(
                new RegistryBuildAccess(
                    "507f1f77bcf86cd799439012",
                    new RegistryPushTarget("harbor.test", "pole-test", repositoryName, tag),
                    "pole-registry-507f1f77bcf86cd799439012",
                    "robot$build",
                    "build-secret"));

        public Task<GetTenantRegistryArtifactOutcome> GetArtifactAsync(
            string tenantId,
            string registryProjectId,
            string repositoryName,
            string reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GetTenantRegistryArtifactOutcome>(
                new RegistryArtifact(repositoryName, reference, Digest, 100));
    }

    private sealed class FakeBuildKitClient
    {
        public bool ThrowUnexpected { get; init; }
        public int Calls { get; private set; }
        public BuildKitBuildRequest? Request { get; private set; }

        public Task<BuildContainerImageOutcome> InvokeAsync(
            BuildKitBuildRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            if (ThrowUnexpected) throw new InvalidOperationException($"unsafe infrastructure detail {request.RegistrySecret}");
            return Task.FromResult<BuildContainerImageOutcome>(
                new BuiltContainerImage(request.Target.Reference, Digest));
        }
    }

    private sealed class RecordingLogger : ILogger<SourceBuildJobExecutor>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception) + exception?.ToString());
    }

    private sealed class FakeDeploymentService(
        DeploySourceBuildWebServiceOutcome? result = null)
    {
        public SourceBuildDeploymentRequest? Request { get; private set; }

        public Task<DeploySourceBuildWebServiceOutcome> DeployAsync(
            SourceBuildDeploymentRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(result ?? new DeploySourceBuildWebServiceOutcome(
                new SourceBuildDeploymentResult("release-1", 2)));
        }
    }
}
