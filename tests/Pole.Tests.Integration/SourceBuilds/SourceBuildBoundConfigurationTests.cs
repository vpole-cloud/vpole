// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Configurations;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

[Collection(IntegrationTestCollection.Global4)]
public sealed class SourceBuildBoundConfigurationTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_ProtectsLiveImageBinding_AndAllowsOwnerCleanup(bool bound)
    {
        var current = SourceBuildConfigurationFactory.Create(
            ObjectId.GenerateNewId().ToString(),
            "application-build",
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            new SourceBuildRecipe("FROM scratch"),
            new ManualSourceBuildConfigurationOrigin("author"),
            SourceBuildConfigurationFactory.DefaultOptions);
        var configurations = Fixture.Database.GetCollection<SourceBuildConfiguration>("SourceBuildConfigurations");
        await configurations.InsertOneAsync(current);
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton(Fixture.Client)
            .AddSingleton(Fixture.Database)
            .AddSingleton<MongoRunTransaction>()
            .AddSingleton(Fixture.StoreOutboxWrites)
            .AddSingleton<IGitOpsMutations>(new ManualAuthority())
            .AddSingleton<LoadSourceBuildTarget>((
                request, session,
                _) =>
            {
                Assert.True(session.IsInTransaction);
                Assert.Equal(current.TenantId, request.TenantId);
                Assert.Equal(current.WebServiceId, request.WebServiceId);
                return Task.FromResult<LoadSourceBuildTargetOutcome>(
                    bound
                        ? new SourceBuildTargetSnapshot(current.TenantId, current.WebServiceId, 1, current.Id)
                        : new SourceBuildWebServiceNotFound(current.WebServiceId));
            })
            .AddSourceBuildsFeature(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var result = await services.GetRequiredService<DeleteSourceBuildConfiguration>()(current.TenantId, current.Id);
        if (bound)
        {
            var inUse = Assert.IsType<SourceBuildConfigurationInUse>(result.Value);
            Assert.Equal(409, SourceBuildHttpMappings.MapConfigurationInUse(inUse).StatusCode);
            var preserved = await configurations.Find(item => item.Id == current.Id).SingleAsync();
            Assert.Equal(current.ReferenceFence, preserved.ReferenceFence);
        }
        else
        {
            Assert.IsType<SourceBuildConfigurationRemovalRecorded>(result.Value);
            Assert.False(await configurations.Find(item => item.Id == current.Id).AnyAsync());
        }

        Assert.Equal(
            bound ? 0 : 1,
            await Fixture.Database
                .GetCollection<BsonDocument>("OutboxMessages")
                .CountDocumentsAsync(new BsonDocument("TenantId", current.TenantId)));
    }

    private sealed class ManualAuthority : IGitOpsMutations
    {
        public Task<bool> AuthorizeAsync(
            AuthorizeGitResourceMutationRequest request,
            IClientSessionHandle session,
            CancellationToken ct) => Task.FromResult(true);

        public Task<GitOperationResult> ExecuteAsync(
            GitOperation operation,
            Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<GitOperationResult?> BeginAsync(
            GitOperation operation,
            IClientSessionHandle session,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<GitOperationResult> CompleteAsync(
            CompleteGitOperationRequest request,
            IClientSessionHandle session,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> ResolveAsync(
            ResolveGitResourceRequest request,
            IClientSessionHandle session,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> ResolveAcceptedAsync(
            ResolveAcceptedGitReferenceRequest request,
            IClientSessionHandle session,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<PinGitReferenceOutcome> PinReferenceAsync(PinGitReferenceRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, GitResourceStatus>> StatusesAsync(
            LoadGitResourceStatusesRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<GitResourceStatus?> StatusAsync(LoadGitResourceStatusRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
