using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitProviders.Application;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Outbox.Contracts;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Application.Execution.Queueing;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Domain;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

public sealed class SourceBuildExecutionRecoveryTests(Pole.Testing.MongoReplicaSetFixture fixture)
    : IClassFixture<Pole.Testing.MongoReplicaSetFixture>
{
    private IMongoCollection<SourceBuildJob> Jobs => fixture.Database.GetCollection<SourceBuildJob>("SourceBuildJobs");

    [Fact]
    public async Task ConcurrentClaim_HeartbeatAndTakeover_FenceTheOldAttempt()
    {
        await using var services = Services();
        var job = Job();
        await Jobs.InsertOneAsync(job);
        var claim = services.GetRequiredService<ClaimQueuedSourceBuildJob>();
        var claimed = Assert.Single((await Task.WhenAll(claim(new(job.Id, "first")),
            claim(new(job.Id, "second")))).OfType<SourceBuildJob>());
        var lease = Assert.IsType<ClaimedSourceBuildExecution>(claimed.Execution);
        var identity = new SourceBuildExecutionIdentity(job.TenantId, job.Id, lease.WorkerId, lease.Attempt);
        var renew = services.GetRequiredService<RenewSourceBuildExecution>();
        Assert.True(await renew(identity));
        Assert.Null(await claim(new(job.Id, "third")));
        var renewed = await Jobs.Find(item => item.Id == job.Id).SingleAsync();
        Assert.Equal(lease.Attempt, ((ClaimedSourceBuildExecution)renewed.Execution).Attempt);
        Assert.True(((ClaimedSourceBuildExecution)renewed.Execution).ExpiresAtUtc >= lease.ExpiresAtUtc);
        Assert.Equal(claimed.UpdatedAtUtc, renewed.UpdatedAtUtc);

        await Jobs.UpdateOneAsync(item => item.Id == job.Id,
            Builders<SourceBuildJob>.Update.Set("Execution.ExpiresAtUtc", DateTime.UtcNow.AddMinutes(-1)));
        var recovered = Assert.IsType<SourceBuildJob>(await claim(new(job.Id, "third")));
        Assert.Equal(lease.Attempt + 1, ((ClaimedSourceBuildExecution)recovered.Execution).Attempt);
        Assert.False(await renew(identity));
        Assert.False(await services.GetRequiredService<TryPersistSourceBuildJob>()(
            claimed, claimed.StartAnalyzing(DateTime.UtcNow)));
        Assert.Equal(job.CommitSha, recovered.CommitSha);
        Assert.Equal(job.Recipe, recovered.Recipe);
        Assert.Equal(job.InputVersion, recovered.InputVersion);
    }

    [Fact]
    public async Task RecoveryScanner_FindsQueuedAndExpiredWork_WithoutDispatchingLiveOrDeployingJobs()
    {
        await using var services = Services();
        var old = DateTime.UtcNow.AddMinutes(-5);
        var queued = Job() with { UpdatedAtUtc = old };
        var expired = ActiveJob("Building", old) with { UpdatedAtUtc = old };
        var live = ActiveJob("Building", DateTime.UtcNow.AddMinutes(2)) with { UpdatedAtUtc = old };
        var deploying = ActiveJob("Deploying", old) with { UpdatedAtUtc = old };
        await Jobs.InsertManyAsync([queued, expired, live, deploying]);
        var recovered = await services.GetRequiredService<LoadStaleQueuedSourceBuildJobs>()(
            DateTime.UtcNow.AddMinutes(-1), 100);
        Assert.Contains(queued.Id, recovered);
        Assert.Contains(expired.Id, recovered);
        Assert.DoesNotContain(live.Id, recovered);
        Assert.DoesNotContain(deploying.Id, recovered);
    }

    [Theory]
    [InlineData("Cloning")]
    [InlineData("Analyzing")]
    [InlineData("Building")]
    [InlineData("Pushing")]
    public async Task ExpiredActiveAttempt_RestartsTheSameImmutableInput(string state)
    {
        await using var services = Services();
        var job = ActiveJob(state, DateTime.UtcNow.AddMinutes(-1));
        await Jobs.InsertOneAsync(job);
        var recovered = Assert.IsType<SourceBuildJob>(
            await services.GetRequiredService<ClaimQueuedSourceBuildJob>()(new(job.Id, "recovered")));
        Assert.IsType<CloningSourceBuild>(recovered.State);
        Assert.Equal(2, ((ClaimedSourceBuildExecution)recovered.Execution).Attempt);
        Assert.Equal(job.CommitSha, recovered.CommitSha);
        Assert.Equal(job.Recipe, recovered.Recipe);
    }

    [Theory]
    [InlineData("Deploying")]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    public async Task DeploymentOrTerminalState_IsNeverRebuilt(string state)
    {
        await using var services = Services();
        var job = ActiveJob(state, DateTime.UtcNow.AddMinutes(-1));
        await Jobs.InsertOneAsync(job);
        Assert.Null(await services.GetRequiredService<ClaimQueuedSourceBuildJob>()(new(job.Id, "other")));
    }

    [Fact]
    public async Task UnleasedActiveJob_IsNotTakenOver()
    {
        await using var services = Services();
        var job = Job().StartCloning(DateTime.UtcNow.AddMinutes(-5));
        await Jobs.InsertOneAsync(job);
        Assert.Null(await services.GetRequiredService<ClaimQueuedSourceBuildJob>()(new(job.Id, "other")));
    }

    [Fact]
    public async Task Promotion_ReceiptAndDeployingStateCommitOnce_AndReplayReturnsTheOriginalRelease()
    {
        await using var services = Services();
        var job = await SeedPromotionAsync();
        var request = Promotion(job);
        var transactions = services.GetRequiredService<MongoRunTransaction>();
        var begin = services.GetRequiredService<BeginSourceBuildPromotion>();
        var complete = services.GetRequiredService<CompleteSourceBuildPromotion>();
        await transactions.InvokeOutcomeAsync<bool>(async (transaction, ct) =>
        {
            Assert.IsType<SourceBuildPromotionAuthorized>((await begin(request, transaction.Session, ct)).Value);
            await complete(new(request, "release-one", 7), transaction.Session, ct);
            return transaction.Commit(true);
        });

        Assert.IsType<DeployingSourceBuild>((await Jobs.Find(item => item.Id == job.Id).SingleAsync()).State);
        await transactions.InvokeOutcomeAsync<bool>(async (transaction, ct) =>
        {
            var replay = Assert.IsType<SourceBuildAlreadyPromoted>(
                (await begin(request, transaction.Session, ct)).Value);
            Assert.Equal("release-one", replay.ReleaseId);
            Assert.Equal(7, replay.Revision);
            return transaction.Commit(true);
        });
        Assert.Equal(1, await fixture.Database
            .GetCollection<SourceBuildPromotionReceipt>("SourceBuildPromotionReceipts")
            .CountDocumentsAsync(item => item.Id == job.Id));
        Assert.Equal(1, await fixture.Database.GetCollection<BsonDocument>("OutboxMessages")
            .CountDocumentsAsync(new BsonDocument("TenantId", job.TenantId)));
    }

    [Fact]
    public async Task Promotion_TransactionFailureRollsBackReceiptAndJobState()
    {
        await using var services = Services();
        var job = await SeedPromotionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<bool>(async (transaction, ct) =>
            {
                var request = Promotion(job);
                Assert.IsType<SourceBuildPromotionAuthorized>((await services
                    .GetRequiredService<BeginSourceBuildPromotion>()(request, transaction.Session, ct)).Value);
                await services.GetRequiredService<CompleteSourceBuildPromotion>()(
                    new(request, "rolled-back-release", 2), transaction.Session, ct);
                throw new InvalidOperationException("Injected transaction failure.");
            }));
        Assert.IsType<PushingSourceBuild>((await Jobs.Find(item => item.Id == job.Id).SingleAsync()).State);
        Assert.Equal(0, await fixture.Database
            .GetCollection<SourceBuildPromotionReceipt>("SourceBuildPromotionReceipts")
            .CountDocumentsAsync(item => item.Id == job.Id));
        Assert.Equal(0, await fixture.Database.GetCollection<BsonDocument>("OutboxMessages")
            .CountDocumentsAsync(new BsonDocument("TenantId", job.TenantId)));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("configuration")]
    [InlineData("lease")]
    [InlineData("tenant")]
    public async Task Promotion_RejectsStaleAuthority(string mismatch)
    {
        await using var services = Services();
        var job = await SeedPromotionAsync();
        var request = Promotion(job);
        request = mismatch switch
        {
            "generation" => request with { Authority = request.Authority with { Generation = 2 } },
            "configuration" => request with { Authority = request.Authority with { ConfigurationRevision = 2 } },
            "lease" => request with { Authority = request.Authority with { Attempt = 2 } },
            "tenant" => request with { TenantId = ObjectId.GenerateNewId().ToString() },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        await services.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync<bool>(async (transaction, ct) =>
        {
            Assert.IsType<SourceBuildPromotionSuperseded>((await services
                .GetRequiredService<BeginSourceBuildPromotion>()(
                request, transaction.Session, ct)).Value);
            return transaction.Commit(true);
        });
    }

    [Theory]
    [InlineData("repository", false)]
    [InlineData("commit", false)]
    [InlineData("reference", false)]
    [InlineData("digest", false)]
    [InlineData("repository", true)]
    [InlineData("commit", true)]
    [InlineData("reference", true)]
    [InlineData("digest", true)]
    public async Task Promotion_RejectsAlteredInputBeforeCommitAndReceiptReplay(string mismatch, bool committed)
    {
        await using var services = Services();
        var job = await SeedPromotionAsync();
        var request = Promotion(job);
        var transactions = services.GetRequiredService<MongoRunTransaction>();
        var begin = services.GetRequiredService<BeginSourceBuildPromotion>();
        if (committed)
            await transactions.InvokeOutcomeAsync<bool>(async (transaction, ct) =>
            {
                Assert.IsType<SourceBuildPromotionAuthorized>((await begin(request, transaction.Session, ct)).Value);
                await services.GetRequiredService<CompleteSourceBuildPromotion>()(
                    new(request, "original-release", 2), transaction.Session, ct);
                return transaction.Commit(true);
            });

        var altered = mismatch switch
        {
            "repository" => request with { Input = request.Input with { SourceRepositoryId = "other-repository" } },
            "commit" => request with { Input = request.Input with { CommitSha = new string('b', 40) } },
            "reference" => request with
            {
                Input = request.Input with
                {
                    Artifact = request.Input.Artifact with { Reference = "other/image:tag" }
                }
            },
            "digest" => request with
            {
                Input = request.Input with
                {
                    Artifact = request.Input.Artifact with { Digest = "sha256:other" }
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        await transactions.InvokeOutcomeAsync<bool>(async (transaction, ct) =>
        {
            Assert.IsType<SourceBuildPromotionSuperseded>((await begin(altered, transaction.Session, ct)).Value);
            return transaction.Abort(true);
        });
        Assert.Equal(committed ? 1 : 0, await fixture.Database
            .GetCollection<SourceBuildPromotionReceipt>("SourceBuildPromotionReceipts")
            .CountDocumentsAsync(item => item.Id == job.Id));
    }

    [Theory]
    [InlineData("repository-revision")]
    [InlineData("connection")]
    [InlineData("tenant")]
    public async Task Promotion_RequiresCurrentRepositoryAndAuthorityFences(string revoked)
    {
        await using var services = Services();
        var job = await SeedPromotionAsync();
        if (revoked == "repository-revision")
            await fixture.Database.GetCollection<SourceRepository>("SourceRepositories").UpdateOneAsync(
                item => item.Id == job.SourceRepositoryId,
                Builders<SourceRepository>.Update.Inc(item => item.Revision, 1));
        else if (revoked == "connection")
            await fixture.Database.GetCollection<GitProviderConnection>("GitProviderConnections")
                .DeleteManyAsync(item => item.TenantId == job.TenantId);
        else
            await fixture.Database.GetCollection<Tenant>("Tenants").DeleteOneAsync(item => item.Id == job.TenantId);

        await services.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync<bool>(async (transaction, ct) =>
        {
            var result = await services.GetRequiredService<BeginSourceBuildPromotion>()(
                Promotion(job), transaction.Session, ct);
            Assert.IsType<SourceBuildPromotionSuperseded>(result.Value);
            return transaction.Abort(true);
        });
        Assert.IsType<PushingSourceBuild>((await Jobs.Find(item => item.Id == job.Id).SingleAsync()).State);
        Assert.Equal(0, await fixture.Database
            .GetCollection<SourceBuildPromotionReceipt>("SourceBuildPromotionReceipts")
            .CountDocumentsAsync(item => item.Id == job.Id));
    }

    private async Task<SourceBuildJob> SeedPromotionAsync()
    {
        var job = ActiveJob("Pushing", DateTime.UtcNow.AddMinutes(2));
        var configuration = SourceBuildConfigurationFactory.Create(
            job.TenantId,
            "application-build",
            job.SourceRepositoryId,
            job.WebServiceId,
            job.Recipe,
            new ManualSourceBuildConfigurationOrigin("author"),
            new(job.ConfigurationId, job.CreatedAtUtc)) with
        {
            BuildGeneration = 1
        };
        await fixture.Database.GetCollection<Tenant>("Tenants").InsertOneAsync(
            TenantFactory.Create("Build tenant", job.TenantId));
        var connection = GitProviderConnectionFactory.Create(job.TenantId, "github-application", GitProviderKind.GitHub,
            "installation", "account", "owner", GitProviderAccountType.User, GitProviderRepositorySelection.All,
            "author", GitProviderConnectionFactory.DefaultOptions);
        await fixture.Database.GetCollection<GitProviderConnection>("GitProviderConnections")
            .InsertOneAsync(connection);
        var repository = SourceRepositoryFactory.Create(
            job.TenantId,
            "application",
            connection.Id,
            "repository",
            "owner/application",
            "https://github.test/owner/application",
            true,
            "main",
            new(job.SourceRepositoryId, job.CreatedAtUtc, job.CreatedAtUtc));
        await fixture.Database.GetCollection<SourceRepository>("SourceRepositories").InsertOneAsync(repository);
        await fixture.Database.GetCollection<SourceBuildConfiguration>("SourceBuildConfigurations")
            .InsertOneAsync(configuration);
        await Jobs.InsertOneAsync(job);
        return job;
    }

    private static SourceBuildPromotionRequest Promotion(SourceBuildJob job) =>
        new(job.TenantId, job.Id, job.WebServiceId, new(job.ConfigurationId, 1, 1, "worker", 1),
            new(job.SourceRepositoryId, job.CommitSha,
                new("registry/application:attempt-one", "sha256:artifact")));

    private ServiceProvider Services() => new ServiceCollection().AddLogging().AddSingleton(fixture.Client)
        .AddSingleton(fixture.Database).AddSingleton(fixture.StoreOutboxWrites).AddSingleton<MongoRunTransaction>()
        .AddTenantsFeature().AddGitProvidersFeature(new ConfigurationBuilder().Build())
        .AddSourceBuildsFeature(new ConfigurationBuilder().Build()).BuildServiceProvider();

    private static SourceBuildJob Job() => SourceBuildJobFactory.CreateManual(
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        "main",
        new string('a', 40),
        new(1, 1, 1),
        new("FROM scratch"),
        "author",
        null,
        SourceBuildJobFactory.DefaultOptions);

    private static SourceBuildJob ActiveJob(string state, DateTime expiry)
    {
        var job = Job() with { Execution = new ClaimedSourceBuildExecution("worker", 1, expiry) };
        var now = job.CreatedAtUtc;
        job = job.StartCloning(now);
        if (state == "Cloning") return job;

        job = job.StartAnalyzing(now);
        if (state == "Analyzing") return job;

        job = job.StartBuilding(now);
        if (state == "Building") return job;

        job = job.StartPushing(SourceBuildArtifactFactory.Create("registry/application:attempt-one",
            "sha256:artifact"), now);
        if (state == "Pushing") return job;

        job = job.StartDeploying(SourceBuildArtifactFactory.Create("registry/application:attempt-one",
            "sha256:artifact"),
            "release-one", now);
        return state switch
        {
            "Deploying" => job,
            "Succeeded" => job.Succeed(now),
            "Failed" => job.Fail(SourceBuildPhase.Deploying, "Failure", "Unavailable.", now),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }
}
