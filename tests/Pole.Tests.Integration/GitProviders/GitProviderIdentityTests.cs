using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Pole.Features.GitProviders.Application;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitProviders.Application.Persistence.Connections;
using Pole.Features.GitProviders.Application.Persistence.Setup;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitProviders;

public sealed class GitProviderIdentityTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task NamesAreTenantScopedImmutableAndResolvedWithoutCredentials()
    {
        await Indexes();
        var attempt = Attempt("production");
        var persistence = Persistence();
        var linked = Assert.IsType<LinkedGitProviderConnection>(
            (await persistence.UpsertConnectionAsync(attempt, Installation("42"), DateTime.UnixEpoch, default)).Value);
        var repeat = await persistence.UpsertConnectionAsync(attempt, Installation("42"), DateTime.UnixEpoch, default);
        Assert.Equal(linked, repeat.Value);
        Assert.IsType<GitProviderConnectionNameMismatch>((await persistence.UpsertConnectionAsync(
            attempt with { TechnicalName = "renamed" }, Installation("42"), DateTime.UnixEpoch, default)).Value);
        Assert.IsType<GitProviderConnectionNameConflict>((await persistence.UpsertConnectionAsync(
            attempt, Installation("43"), DateTime.UnixEpoch, default)).Value);

        var foreignTenant = ObjectId.GenerateNewId().ToString();
        using var provider = new ServiceCollection().AddSingleton(Fixture.Database)
            .AddGitProvidersFeature(new ConfigurationBuilder().Build()).BuildServiceProvider();
        var finder = provider.GetRequiredService<FindNamedGitProviderConnection>();
        Assert.Equal(new NamedGitProviderConnectionFound(linked.Id),
            (await finder(new(attempt.TenantId, attempt.TechnicalName))).Value);
        Assert.IsType<NamedGitProviderConnectionNotFound>(
            (await finder(new(foreignTenant, attempt.TechnicalName))).Value);
        Assert.IsType<LinkedGitProviderConnection>((await persistence.UpsertConnectionAsync(
            attempt with { TenantId = foreignTenant }, Installation("44"), DateTime.UnixEpoch, default)).Value);
        Assert.Equal(2, await DbContext.GitProviderConnections.CountDocumentsAsync(_ => true));
        Assert.Equal(2, await DbContext.CountOutboxMessagesAsync(
            "pole.git-providers/v1/git-provider-connection-changed"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentNamesHaveOneOwnerAndOnlyAcceptedOutbox(bool sameInstallation)
    {
        await Indexes();
        var attempt = Attempt("production");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            Persistence().UpsertConnectionAsync(attempt,
                Installation(sameInstallation ? "42" : (42 + index).ToString()), DateTime.UnixEpoch, default)));

        Assert.Equal(1, await DbContext.GitProviderConnections.CountDocumentsAsync(_ => true));
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(
            "pole.git-providers/v1/git-provider-connection-changed"));
        Assert.Equal(sameInstallation ? 4 : 1, outcomes.Count(item => item.Value is LinkedGitProviderConnection));
        Assert.All(outcomes.Where(item => item.Value is not LinkedGitProviderConnection),
            item => Assert.IsType<GitProviderConnectionNameConflict>(item.Value));
    }

    private GitHubSetupPersistence Persistence() =>
        new(Fixture.Database.GetCollection<GitHubSetupAttempt>("GitHubSetupAttempts"),
            DbContext.GitProviderConnections,
            Services.GetRequiredService<MongoRunTransaction>(), Fixture.StoreOutboxWrites);

    private Task Indexes() => DbContext.GitProviderConnections.Indexes.CreateManyAsync(
    [
        new CreateIndexModel<GitProviderConnection>(
            Builders<GitProviderConnection>.IndexKeys.Ascending(item => item.TenantId)
                .Ascending(item => item.TechnicalName),
            new CreateIndexOptions { Unique = true, Name = GitProviderConnectionIndexes.TechnicalName }),
        new CreateIndexModel<GitProviderConnection>(
            Builders<GitProviderConnection>.IndexKeys.Ascending(item => item.Provider)
                .Ascending(item => item.ExternalInstallationId),
            new CreateIndexOptions { Unique = true, Name = GitProviderConnectionIndexes.Installation })
    ]);

    private static GitHubSetupAttempt Attempt(string name) => GitHubSetupAttemptFactory.Create(
        TestData.TenantId, TestData.UserId, name, "state", GitHubSetupAttemptFactory.DefaultOptions);

    private static GitHubInstallationMetadata Installation(string id) =>
        new(id, "account", "display", GitProviderAccountType.Organization, GitProviderRepositorySelection.All);
}
