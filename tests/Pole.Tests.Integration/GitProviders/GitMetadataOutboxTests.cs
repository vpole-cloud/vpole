using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Connections;
using Pole.Features.GitProviders.Application.Repositories;
using Pole.Features.GitProviders.Application.Setup;
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
        var persistence = SetupPersistence(Fixture.StoreOutboxWrites);
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
        await RepositoryIndexes();
        var repository = Repository();
        await SeedConnection(repository);
        var link = Link(Fixture.StoreOutboxWrites);
        Assert.IsType<SourceRepository>((await link.InvokeAsync(repository)).Value);
        Assert.IsType<SourceRepositoryNameConflict>((await link.InvokeAsync(repository with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Branch = "another"
        })).Value);
        Assert.IsType<SourceRepositoryAlreadyLinked>((await link.InvokeAsync(repository with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            TechnicalName = "another"
        })).Value);
        var stored = await DbContext.SourceRepositories.Find(row => row.Id == repository.Id).SingleAsync();
        var delete = Delete(Fixture.StoreOutboxWrites);
        Assert.IsType<SourceRepositoryDeleted>((await delete.InvokeAsync(stored.TenantId, stored.Id)).Value);
        Assert.IsType<SourceRepositoryNotFound>((await delete.InvokeAsync(stored.TenantId, stored.Id)).Value);
        Assert.Equal(2, await DbContext.CountOutboxMessagesAsync(RepositoryEvent));
        Assert.All(await DbContext.OutboxMessages.Find(row => row.EventType == RepositoryEvent).ToListAsync(),
            row =>
            {
                Assert.DoesNotContain("private-repository-name", row.PayloadJson);
                Assert.DoesNotContain("private-plan-content", row.PayloadJson);
            });
    }

    [Fact]
    public async Task ConcurrentRepositoryNamesHaveOneOwnerAndOneOutboxEvent()
    {
        await RepositoryIndexes();
        var repository = Repository();
        await SeedConnection(repository);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Link(Fixture.StoreOutboxWrites)
            .InvokeAsync(repository with { Id = ObjectId.GenerateNewId().ToString(), Branch = $"branch-{index}" })));
        Assert.Single(outcomes, outcome => outcome.Value is SourceRepository);
        Assert.Equal(3, outcomes.Count(outcome => outcome.Value is SourceRepositoryNameConflict));
        Assert.Equal(1, await DbContext.SourceRepositories.CountDocumentsAsync(_ => true));
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(RepositoryEvent));
    }

    [Theory]
    [InlineData("connection-create")]
    [InlineData("connection-update")]
    [InlineData("connection-delete")]
    [InlineData("repository-create")]
    [InlineData("repository-delete")]
    public async Task OutboxFailureRollsBackLocalGitMutation(string mutation)
    {
        var attempt = Attempt();
        var metadata = Metadata();
        var now = DateTime.UtcNow;
        string? connectionId = null;
        if (mutation is "connection-update" or "connection-delete")
            connectionId = Assert.IsType<LinkedGitProviderConnection>((
                await SetupPersistence(Fixture.StoreOutboxWrites)
                    .UpsertConnectionAsync(attempt, metadata, now, default)).Value).Id;
        var repository = Repository();
        if (mutation is "repository-create" or "repository-delete")
            await SeedConnection(repository);
        if (mutation is "repository-delete")
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
            "connection-create" or "connection-update" => SetupPersistence(failing)
                .UpsertConnectionAsync(attempt,
                    metadata with
                    {
                        AccountLogin = "replacement"
                    },
                    now.AddSeconds(1),
                    default),
            "connection-delete" => new MongoDeleteGitProviderConnection(
                Fixture.DbContext.GitProviderConnections, Fixture.DbContext.SourceRepositories, Transactions, failing)
                .InvokeAsync(attempt.TenantId, connectionId!),
            "repository-create" => Link(failing)
                .InvokeAsync(repository),
            _ => Delete(failing).InvokeAsync(
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

    private GitHubSetupPersistence SetupPersistence(StoreOutboxWrites outbox) =>
        new(Fixture.Database.GetCollection<GitHubSetupAttempt>("GitHubSetupAttempts"),
            DbContext.GitProviderConnections,
            Transactions,
            outbox);

    private MongoPersistLinkedSourceRepository Link(StoreOutboxWrites outbox) =>
        new(DbContext.SourceRepositories, DbContext.GitProviderConnections, Transactions, outbox);

    private MongoDeleteSourceRepository Delete(StoreOutboxWrites outbox) =>
        new(DbContext.SourceRepositories, Transactions, outbox,
            Services.GetRequiredService<EnsureNoSourceRepositoryBuildConfiguration>(),
            Services.GetRequiredService<IGitOpsMutations>());

    private Task SeedConnection(SourceRepository repository) => DbContext.GitProviderConnections.InsertOneAsync(
        GitProviderConnectionFactory.Create(repository.TenantId, "repository-connection", GitProviderKind.GitHub,
            "42", "account", "organization", GitProviderAccountType.Organization, GitProviderRepositorySelection.All,
            TestData.UserId, GitProviderConnectionFactory.DefaultOptions with { Id = repository.ConnectionId }));

    private Task RepositoryIndexes() => DbContext.SourceRepositories.Indexes.CreateManyAsync(
    [
        new CreateIndexModel<SourceRepository>(Builders<SourceRepository>.IndexKeys
            .Ascending(item => item.TenantId).Ascending(item => item.TechnicalName),
            new CreateIndexOptions { Unique = true, Name = SourceRepositoryIndexes.TechnicalName }),
        new CreateIndexModel<SourceRepository>(Builders<SourceRepository>.IndexKeys
            .Ascending(item => item.TenantId).Ascending(item => item.ConnectionId)
            .Ascending(item => item.ExternalRepositoryId).Ascending(item => item.Branch),
            new CreateIndexOptions { Unique = true, Name = SourceRepositoryIndexes.Location })
    ]);

    private static SourceRepository Repository() => SourceRepositoryFactory.Create(
        TestData.TenantId, "application", ObjectId.GenerateNewId().ToString(), "external",
        "private-repository-name", "private-url", true, "main", SourceRepositoryFactory.DefaultOptions);
}
