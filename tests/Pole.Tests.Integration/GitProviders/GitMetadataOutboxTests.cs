using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Pole.Database;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence.Connections;
using Pole.Features.GitProviders.Application.Persistence.Repositories;
using Pole.Features.GitProviders.Application.Persistence.Setup;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Outbox.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitProviders;

public sealed class GitMetadataOutboxTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private const string ConnectionEvent = "pole.git-providers/v1/git-provider-connection-changed";
    private const string RepositoryEvent = "pole.git-providers/v1/source-repository-changed";
    private MongoRunTransaction Transactions => Services.GetRequiredService<MongoRunTransaction>();

    [Fact]
    public async Task ConnectionUpsertRejectsForeignTenantAndLeavesIdenticalMetadataQuiet()
    {
        var attempt = Attempt();
        var metadata = Metadata();
        var now = DateTime.UtcNow;
        var persistence = new GitHubSetupPersistence(Fixture.Database, Transactions, Fixture.StoreOutboxWrites);
        var id = await persistence.UpsertConnectionAsync(attempt, metadata, now, default);
        Assert.IsType<LinkedGitProviderConnection>(id.Value);
        Assert.Equal(id, await persistence.UpsertConnectionAsync(attempt, metadata, now, default));
        Assert.IsType<GitProviderConnectionOwnershipRejected>((await persistence.UpsertConnectionAsync(
            attempt with
            {
                TenantId = ObjectId.GenerateNewId().ToString()
            },
            metadata,
            now,
            default)).Value);
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(ConnectionEvent));
        await persistence.UpsertConnectionAsync(attempt,
            metadata with
            {
                AccountLogin = "private-account-name"
            },
            now.AddSeconds(1),
            default);
        Assert.Equal(2, await DbContext.CountOutboxMessagesAsync(ConnectionEvent));
        Assert.All(await DbContext.OutboxMessages.Find(row => row.EventType == ConnectionEvent).ToListAsync(),
            row =>
            {
                Assert.Equal(attempt.TenantId, row.TenantId);
                Assert.DoesNotContain("private-account-name", row.PayloadJson);
                Assert.DoesNotContain("private-state", row.PayloadJson);
            });
    }

    [Fact]
    public async Task RepositoryCrudAndRepeatedDeletesEmitOnlyAcceptedIdHints()
    {
        var repository = Repository();
        var link = new MongoPersistLinkedSourceRepository(Fixture.Database, Transactions, Fixture.StoreOutboxWrites);
        Assert.IsType<SourceRepository>((await link.InvokeAsync(repository)).Value);
        Assert.IsType<SourceRepositoryAlreadyLinked>((await link.InvokeAsync(repository)).Value);
        var stored = await DbContext.SourceRepositories.Find(row => row.Id == repository.Id).SingleAsync();
        var reanalyze =
            new MongoPersistReanalyzedSourceRepository(Fixture.Database, Transactions, Fixture.StoreOutboxWrites);
        await reanalyze.InvokeAsync(stored,
            Provider(stored),
            Analysis(stored),
            stored.SelectedPlan,
            stored.UpdatedAtUtc);
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(RepositoryEvent));
        await reanalyze.InvokeAsync(stored,
            Provider(stored),
            Analysis(stored) with
            {
                CommitSha = "new-commit"
            },
            stored.SelectedPlan,
            stored.UpdatedAtUtc.AddSeconds(1));
        var delete = new MongoDeleteSourceRepository(Fixture.Database, Transactions, Fixture.StoreOutboxWrites);
        Assert.IsType<SourceRepositoryDeleted>((await delete.InvokeAsync(stored.TenantId, stored.Id)).Value);
        Assert.IsType<SourceRepositoryNotFound>((await delete.InvokeAsync(stored.TenantId, stored.Id)).Value);
        Assert.Equal(3, await DbContext.CountOutboxMessagesAsync(RepositoryEvent));
        Assert.All(await DbContext.OutboxMessages.Find(row => row.EventType == RepositoryEvent).ToListAsync(),
            row =>
            {
                Assert.DoesNotContain("private-repository-name", row.PayloadJson);
                Assert.DoesNotContain("private-plan-content", row.PayloadJson);
            });
    }

    [Theory]
    [InlineData("connection-create")]
    [InlineData("connection-update")]
    [InlineData("connection-delete")]
    [InlineData("repository-create")]
    [InlineData("repository-update")]
    [InlineData("repository-delete")]
    public async Task OutboxFailureRollsBackLocalGitMutation(string mutation)
    {
        var attempt = Attempt();
        var metadata = Metadata();
        var now = DateTime.UtcNow;
        string? connectionId = null;
        if (mutation is "connection-update" or "connection-delete")
            connectionId = Assert.IsType<LinkedGitProviderConnection>((
                await new GitHubSetupPersistence(Fixture.Database, Transactions, Fixture.StoreOutboxWrites)
                    .UpsertConnectionAsync(attempt, metadata, now, default)).Value).Id;
        var repository = Repository();
        if (mutation is "repository-update" or "repository-delete")
            await DbContext.SourceRepositories.InsertOneAsync(repository);
        var before = await State();
        var beforeCount = await DbContext.OutboxMessages.CountDocumentsAsync(_ => true);
        StoreOutboxWrites failing = async (session, writes, ct) =>
        {
            await Fixture.StoreOutboxWrites(session, writes, ct);
            throw new InvalidOperationException("Injected outbox failure.");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutation switch
        {
            "connection-create" or "connection-update" => new GitHubSetupPersistence(Fixture.Database,
                    Transactions,
                    failing)
                .UpsertConnectionAsync(attempt,
                    metadata with
                    {
                        AccountLogin = "replacement"
                    },
                    now.AddSeconds(1),
                    default),
            "connection-delete" => new MongoDeleteGitProviderConnection(Fixture.Database, Transactions, failing)
                .InvokeAsync(attempt.TenantId, connectionId!),
            "repository-create" => new MongoPersistLinkedSourceRepository(Fixture.Database, Transactions, failing)
                .InvokeAsync(repository),
            "repository-update" => new MongoPersistReanalyzedSourceRepository(Fixture.Database, Transactions, failing)
                .InvokeAsync(repository,
                    Provider(repository),
                    Analysis(repository) with
                    {
                        CommitSha = "new-commit"
                    },
                    repository.SelectedPlan,
                    now.AddSeconds(1)),
            _ => new MongoDeleteSourceRepository(Fixture.Database, Transactions, failing).InvokeAsync(
                repository.TenantId,
                repository.Id)
        });
        Assert.Equal(before, await State());
        Assert.Equal(beforeCount, await DbContext.OutboxMessages.CountDocumentsAsync(_ => true));
    }

    private async Task<string[]> State()
    {
        var rows = new List<string>();
        foreach (var name in new[] { "GitProviderConnections", "SourceRepositories" })
            rows.Add((await Fixture.Database.GetCollection<BsonDocument>(name)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync()).ToJson());
        return rows.ToArray();
    }

    private static GitHubSetupAttempt Attempt() => GitHubSetupAttemptFactory.Create(TestData.TenantId,
        TestData.UserId,
        "github-test",
        "private-state",
        GitHubSetupAttemptFactory.DefaultOptions);

    private static GitHubInstallationMetadata Metadata() => new("installation",
        "account",
        "private-account",
        GitProviderAccountType.User,
        GitProviderRepositorySelection.Selected);

    private static SourceRepository Repository()
    {
        var now = DateTime.UtcNow;
        var plan = new SourceBuildPlan("candidate",
            SourceBuildPlanKind.Dockerfile,
            "plan",
            ".",
            "Dockerfile",
            SourceBuildPlanConfidence.High,
            ImmutableList<string>.Empty,
            ImmutableList<string>.Empty,
            ImmutableList<string>.Empty,
            null,
            "private-plan-content");
        return SourceRepositoryFactory.Create(TestData.TenantId,
            ObjectId.GenerateNewId().ToString(),
            "external",
            "private-repository-name",
            "private-url",
            true,
            new("main", "commit", now, ImmutableList<string>.Empty, ImmutableList.Create(plan)),
            plan,
            new(ObjectId.GenerateNewId().ToString(), now, now));
    }

    private static ProviderRepository Provider(SourceRepository row) => new(row.ExternalRepositoryId,
        "name",
        row.RepositoryFullName,
        row.RepositoryUrl,
        row.IsPrivate,
        row.Branch,
        row.UpdatedAtUtc);

    private static SourceRepositoryAnalysis Analysis(SourceRepository row) => new(row.Branch,
        row.CommitSha,
        row.AnalyzedAtUtc,
        ImmutableList<string>.Empty,
        ImmutableList.Create(row.SelectedPlan));
}
