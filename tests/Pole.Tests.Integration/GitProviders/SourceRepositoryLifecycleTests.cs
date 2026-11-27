// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitProviders;

[Collection(IntegrationTestCollection.Global)]
public sealed class SourceRepositoryLifecycleTests(
    MongoReplicaSetFixture fixture
) : IsolatedMediatorIntegrationTestBase(fixture)
{
    private ReviewedLifecycleAuthority? _authorization;

    private ReviewedLifecycleAuthority authorization =>
        _authorization ??= new ReviewedLifecycleAuthority(Fixture.Client, Data.TenantId);

    private IMongoCollection<SourceRepository> Repositories =>
        Fixture.Database.GetCollection<SourceRepository>("SourceRepositories");

    private IDeclarativeResourceLifecycleProvider Provider =>
        Services.GetServices<IDeclarativeResourceLifecycleProvider>().Single(item => item.Kind == "SourceRepository");

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IGitOpsMutations>(authorization);

    [Fact]
    public async Task Repository_removal_and_outbox_share_the_callers_transaction()
    {
        var repository = Repository();
        await Repositories.InsertOneAsync(repository);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider.ReadLifecycleAsync(Read(repository), session, default)).Value);
        var request = new RemoveLifecycleResourceRequest(authorization.Authority, snapshot.Resource, snapshot.Version);
        Assert.IsType<GitLifecycleResourceRemoved>((await Provider.RemoveAsync(request, session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(1, await Repositories.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await DbContext.CountAllOutboxMessagesAsync("pole.git-providers/v1/source-repository-changed"));
        session.StartTransaction();
        Assert.IsType<GitLifecycleResourceRemoved>((await Provider.RemoveAsync(request, session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.Equal(0, await Repositories.CountDocumentsAsync(_ => true));
        Assert.Equal(1, await DbContext.CountAllOutboxMessagesAsync("pole.git-providers/v1/source-repository-changed"));
    }

    [Fact]
    public async Task Existing_build_configuration_blocks_repository_removal()
    {
        var repository = Repository();
        await Repositories.InsertOneAsync(repository);
        var configuration = SourceBuildConfigurationFactory.Create(
            repository.TenantId,
            "build",
            repository.Id,
            ObjectId.GenerateNewId().ToString(),
            SourceBuildRecipe.Create("FROM scratch"),
            new ManualSourceBuildConfigurationOrigin(Data.UserId),
            SourceBuildConfigurationFactory.DefaultOptions);
        await Fixture.Database
            .GetCollection<SourceBuildConfiguration>("SourceBuildConfigurations")
            .InsertOneAsync(configuration);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider.ReadLifecycleAsync(Read(repository), session, default)).Value);
        var blocked = Assert.IsType<DeclarationReadBlocked>(
            (await Provider.RemoveAsync(
                new RemoveLifecycleResourceRequest(authorization.Authority, snapshot.Resource, snapshot.Version),
                session,
                default)).Value);
        Assert.IsType<GitLifecycleDependencyRetained>(blocked.Failure);
        await session.AbortTransactionAsync();
        Assert.Equal(0, (await Repositories.Find(_ => true).SingleAsync()).ReferenceFence);
    }

    [Fact]
    public async Task Pinned_connection_adoption_preview_preserves_ids_and_does_not_write()
    {
        var connection = GitProviderConnectionFactory.Create(
            Data.TenantId,
            "github-app",
            GitProviderKind.GitHub,
            "installation",
            "account",
            "login",
            GitProviderAccountType.User,
            GitProviderRepositorySelection.All,
            Data.UserId,
            GitProviderConnectionFactory.DefaultOptions);
        await Fixture.Database
            .GetCollection<GitProviderConnection>("GitProviderConnections")
            .InsertOneAsync(connection);
        var repository = Repository() with { ConnectionId = connection.Id };
        await Repositories.InsertOneAsync(repository);
        var declaration = new CompiledDeclaration(
            new ResourceAddress("SourceRepository", repository.TechnicalName),
            DeclarationJson.Write(new { connection = connection.TechnicalName, repositoryId = "42", branch = "main" }),
            "hash",
            [],
            "repository.yaml",
            1);
        using var session = await Fixture.Client.StartSessionAsync();
        var preview = Assert.IsType<GitLifecycleAdoptionPreview>(
            (await ((IDeclarativeAdoptionPreviewProvider)Provider).PreviewAdoptionAsync(
                new PreviewLifecycleAdoptionRequest(
                    Data.TenantId,
                    declaration,
                    repository.Id,
                    [
                        new GitPreviewResolvedReference(
                            new ResourceAddress("GitProviderConnection", connection.TechnicalName),
                            connection.Id),
                    ]),
                session,
                default)).Value);
        Assert.Equal(repository.Id, preview.Current.ResourceId);
        Assert.True(JsonElement.DeepEquals(preview.Current.Projection.Identity, preview.Desired.Identity));
        Assert.Equal(connection.Id, Assert.Single(preview.ResolvedReferences).ResourceId);
        Assert.Equal(0, (await Repositories.Find(_ => true).SingleAsync()).ReferenceFence);
        Assert.Equal(0, await DbContext.CountAllOutboxMessagesAsync("pole.git-providers/v1/source-repository-changed"));
        var rejected = await ((IDeclarativeAdoptionPreviewProvider)Provider).PreviewAdoptionAsync(
            new PreviewLifecycleAdoptionRequest(
                Data.TenantId,
                declaration,
                repository.Id,
                [
                    new GitPreviewResolvedReference(
                        new ResourceAddress("GitProviderConnection", connection.TechnicalName),
                        ObjectId.GenerateNewId().ToString()),
                ]),
            session,
            default);
        Assert.IsType<DeclarationReadBlocked>(rejected.Value);
    }

    private SourceRepository Repository() => SourceRepositoryFactory.Create(
        Data.TenantId,
        "app-repository",
        ObjectId.GenerateNewId().ToString(),
        "42",
        "owner/application",
        "https://example.test/owner/application",
        true,
        "main",
        SourceRepositoryFactory.DefaultOptions);

    private static ReadLifecycleResourceRequest Read(SourceRepository item) => new(
        item.TenantId,
        new GitLifecycleResourceReference(new ResourceAddress("SourceRepository", item.TechnicalName), item.Id));
}
