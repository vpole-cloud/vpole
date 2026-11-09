using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Repositories;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.Tenants.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitProviders;

[Collection(IntegrationTestCollection.Global2)]
public sealed class SourceRepositoryReferenceTests(MongoReplicaSetFixture fixture)
    : IsolatedMediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task NamedReadsAreTenantScopedAndFencesDoNotChangeBusinessMetadata()
    {
        var repository = await SeedAsync(Repository());
        var references = References();
        using var session = await Fixture.Database.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<NamedSourceRepositoryNotFound>((await references.FindNamedAsync(
            new("foreign", repository.TechnicalName), session, default)).Value);
        var found = Assert.IsType<NamedSourceRepositoryFound>((await references.FindNamedAsync(
            new(repository.TenantId, repository.TechnicalName), session, default)).Value);
        Assert.Equal(repository.Id, found.Repository.Id);
        Assert.IsType<SourceRepositoryReferenceConflict>((await references.FenceAsync(
            new(repository.TenantId, repository.Id, repository.Revision + 1), session, default)).Value);
        Assert.IsType<SourceRepositoryFenced>((await references.FenceAsync(
            new(repository.TenantId, repository.Id, repository.Revision), session, default)).Value);
        await session.CommitTransactionAsync();
        var stored = await DbContext.SourceRepositories.Find(item => item.Id == repository.Id).SingleAsync();
        Assert.Equal(repository with { ReferenceFence = 1 }, stored);
        Assert.Equal(1, (await DbContext.GitProviderConnections.Find(item => item.Id == repository.ConnectionId)
            .SingleAsync()).ReferenceFence);
        Assert.Equal(1,
            (await DbContext.Tenants.Find(item => item.Id == repository.TenantId).SingleAsync()).ReferenceFence);
    }

    [Fact]
    public async Task DeletionInvalidatesAnAdmissionSnapshotBeforeItsFence()
    {
        var repository = await SeedAsync(Repository());
        var references = References();
        using var admission = await Fixture.Database.Client.StartSessionAsync();
        admission.StartTransaction();
        Assert.IsType<SourceRepositoryBuildSnapshot>((await references.LoadAsync(
            new(repository.TenantId, repository.Id), admission, default)).Value);
        var deletion = Delete();
        Assert.IsType<SourceRepositoryDeleted>((await deletion.InvokeAsync(repository.TenantId, repository.Id)).Value);

        var error = await Assert.ThrowsAsync<MongoCommandException>(() => references.FenceAsync(
            new(repository.TenantId, repository.Id, repository.Revision), admission, default));
        Assert.True(error.HasErrorLabel("TransientTransactionError"));
        await admission.AbortTransactionAsync();
        Assert.False(await DbContext.SourceBuildConfigurations.Find(
            item => item.SourceRepositoryId == repository.Id).AnyAsync());
    }

    [Fact]
    public async Task AcceptedConfigurationPreventsDeletionAndRollsBackItsTechnicalFence()
    {
        var repository = await SeedAsync(Repository());
        var references = References();
        using var admission = await Fixture.Database.Client.StartSessionAsync();
        admission.StartTransaction();
        Assert.IsType<SourceRepositoryFenced>((await references.FenceAsync(
            new(repository.TenantId, repository.Id, repository.Revision), admission, default)).Value);
        var configuration = SourceBuildConfigurationFactory.Create(repository.TenantId, "application-build",
            repository.Id, ObjectId.GenerateNewId().ToString(), SourceBuildRecipe.Create("FROM scratch"),
            new ManualSourceBuildConfigurationOrigin(Data.UserId), SourceBuildConfigurationFactory.DefaultOptions);
        await DbContext.SourceBuildConfigurations.InsertOneAsync(admission, configuration);
        await admission.CommitTransactionAsync();
        var before = await DbContext.SourceRepositories.Find(item => item.Id == repository.Id).SingleAsync();

        Assert.IsType<SourceRepositoryHasBuildConfiguration>(
            (await Delete().InvokeAsync(repository.TenantId, repository.Id)).Value);
        Assert.Equal(before, await DbContext.SourceRepositories.Find(item => item.Id == repository.Id).SingleAsync());
        Assert.Equal(0, await DbContext.CountAllOutboxMessagesAsync("pole.git-providers/v1/source-repository-changed"));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("connection")]
    public async Task PromotionFenceRejectsConcurrentAuthorityDeletion(string dependency)
    {
        var repository = await SeedAsync(Repository());
        var references = References();
        using var session = await Fixture.Database.Client.StartSessionAsync();
        session.StartTransaction();
        await references.LoadAsync(new(repository.TenantId, repository.Id), session, default);
        await RemoveAuthorityAsync(repository, dependency);
        var failure = await Assert.ThrowsAsync<MongoCommandException>(() => references.FenceAsync(
            new(repository.TenantId, repository.Id, repository.Revision), session, default));
        Assert.True(failure.HasErrorLabel("TransientTransactionError"));
        await session.AbortTransactionAsync();
        var unchanged = await DbContext.SourceRepositories.Find(item => item.Id == repository.Id).SingleAsync();
        Assert.Equal(repository, unchanged);
        Assert.Equal(0, await DbContext.CountAllOutboxMessagesAsync("pole.git-providers/v1/source-repository-changed"));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("connection")]
    public async Task PromotionFenceReturnsConflictWhenAuthorityIsAlreadyGone(string dependency)
    {
        var repository = await SeedAsync(Repository());
        await RemoveAuthorityAsync(repository, dependency);
        using var session = await Fixture.Database.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<SourceRepositoryReferenceConflict>((await References().FenceAsync(
            new(repository.TenantId, repository.Id, repository.Revision), session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(repository,
            await DbContext.SourceRepositories.Find(item => item.Id == repository.Id).SingleAsync());
        if (dependency == "connection")
            Assert.Equal(0, (await DbContext.Tenants.Find(item => item.Id == repository.TenantId).SingleAsync())
                .ReferenceFence);
    }

    private async Task RemoveAuthorityAsync(SourceRepository repository, string dependency)
    {
        if (dependency == "tenant")
            await DbContext.Tenants.DeleteOneAsync(item => item.Id == repository.TenantId);
        else
            await DbContext.GitProviderConnections.DeleteOneAsync(item => item.Id == repository.ConnectionId);
    }

    private async Task<SourceRepository> SeedAsync(SourceRepository repository)
    {
        await DbContext.GitProviderConnections.InsertOneAsync(GitProviderConnectionFactory.Create(
            repository.TenantId, "production-git", GitProviderKind.GitHub, "installation", "account", "organization",
            GitProviderAccountType.Organization, GitProviderRepositorySelection.All, Data.UserId,
            GitProviderConnectionFactory.DefaultOptions with { Id = repository.ConnectionId }));
        await DbContext.SourceRepositories.InsertOneAsync(repository);
        // Compare mutations with the stored BSON baseline, including its millisecond timestamp precision.
        return await DbContext.SourceRepositories.Find(item => item.Id == repository.Id).SingleAsync();
    }

    private MongoSourceRepositoryReferences References() => new(DbContext.SourceRepositories,
        DbContext.GitProviderConnections, Services.GetRequiredService<FenceTenantIdentity>());

    private MongoDeleteSourceRepository Delete() => new(DbContext.SourceRepositories,
        Services.GetRequiredService<MongoRunTransaction>(), Fixture.StoreOutboxWrites,
        Services.GetRequiredService<EnsureNoSourceRepositoryBuildConfiguration>(),
        Services.GetRequiredService<IGitOpsMutations>());

    private SourceRepository Repository() => SourceRepositoryFactory.Create(
        Data.TenantId, "application", ObjectId.GenerateNewId().ToString(), "42", "organization/application",
        "https://github.test/organization/application", true, "main", SourceRepositoryFactory.DefaultOptions);
}
