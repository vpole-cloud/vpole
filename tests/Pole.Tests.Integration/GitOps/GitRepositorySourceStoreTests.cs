// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class GitRepositorySourceStoreTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private GitRepositorySources Sources => Services.GetRequiredService<GitRepositorySources>();

    private IMongoCollection<RepositorySource> Documents =>
        Services.GetRequiredService<IMongoCollection<RepositorySource>>();

    private IMongoCollection<GitSynchronizationState> States =>
        Services.GetRequiredService<IMongoCollection<GitSynchronizationState>>();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
    }

    [Fact]
    public async Task DatabaseClockReadsTypedUtcTimeWithinTheTenantScopedTransaction()
    {
        var source = Assert.IsType<RepositorySource>((await Sources.CreateAsync(Request(), default)).Value);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var before = DateTime.UtcNow.AddSeconds(-5);

        var now = await new GitDatabaseClock(Documents).NowAsync(
            new GitSourceIdentity(source.TenantId, source.Id),
            session,
            default);

        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.InRange(now, before, DateTime.UtcNow.AddSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitDatabaseClock(Documents).NowAsync(
            new GitSourceIdentity(ObjectId.GenerateNewId().ToString(), source.Id),
            session,
            default));
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task ConcurrentCreationIsIdempotentAndDifferentInputConflicts()
    {
        var request = Request();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Sources.CreateAsync(request, default)));
        var ids = results.Select(result => Assert.IsType<RepositorySource>(result.Value).Id).Distinct().ToArray();
        var id = Assert.Single(ids);
        var state = await Sources.StateAsync(Data.TenantId, id, default);
        Assert.Equal(1, state.RequestedGeneration);
        Assert.Equal(0, state.CompletedGeneration);
        Assert.Null(state.CurrentRun);
        Assert.Equal(1, await WakeCount());

        Assert.IsType<GitSourceIdempotencyConflict>(
            (await Sources.CreateAsync(
                request with
                {
                    Repository = request.Repository with { Branch = "other" },
                },
                default)).Value);
        Assert.Equal(1, await Documents.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
        Assert.Equal(1, await WakeCount());
    }

    [Fact]
    public async Task Prune_is_explicit_and_is_part_of_creation_idempotency()
    {
        var request = Request() with { Prune = GitPrunePolicy.Prune };
        var source = Assert.IsType<RepositorySource>((await Sources.CreateAsync(request, default)).Value);
        Assert.Equal(GitPrunePolicy.Prune, source.Prune);
        Assert.IsType<GitSourceIdempotencyConflict>(
            (await Sources.CreateAsync(
                request with
                {
                    Prune = GitPrunePolicy.Retain,
                },
                default)).Value);
        Assert.Equal(1, await Documents.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
    }

    [Fact]
    public async Task SourceAndSynchronizationRollBackWhenOutboxFails()
    {
        var failing = new GitRepositorySources(
            Documents,
            States,
            Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>(),
            Services.GetRequiredService<IMongoCollection<GitSourceRequestReceipt>>(),
            Services.GetRequiredService<MongoRunTransaction>(),
            (
                _, _,
                _) => throw new InvalidOperationException("Injected outbox failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.CreateAsync(Request(), default));
        Assert.Equal(0, await Documents.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
        Assert.Equal(0, await States.CountDocumentsAsync(item => item.Source.TenantId == Data.TenantId));
        Assert.Equal(0, await WakeCount());
    }

    [Fact]
    public async Task ConcurrentSyncHasOneGenerationAndTenantAndRevisionAreEnforced()
    {
        var source = Assert.IsType<RepositorySource>((await Sources.CreateAsync(Request(), default)).Value);
        var request = new GitSourceCommand(
            source.TenantId,
            source.Id,
            source.Revision,
            Guid.NewGuid().ToString("D"),
            new GitRunInitiator(GitRunTrigger.Manual, Data.UserId));
        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Sources.RequestSyncAsync(request, default)));
        Assert.All(results, result => Assert.Equal(2, Assert.IsType<GitSourceScheduled>(result.Value).Generation));
        Assert.Equal(2, (await Sources.StateAsync(source.TenantId, source.Id, default)).RequestedGeneration);
        Assert.Equal(2, await WakeCount());
        Assert.IsType<GitSourceNotFound>(
            (await Sources.RequestSyncAsync(
                request with
                {
                    TenantId = "foreign",
                },
                default)).Value);
        Assert.IsType<GitSourceRevisionConflict>(
            (await Sources.RequestSyncAsync(
                request with
                {
                    ExpectedRevision = 2,
                    RequestKey = Guid.NewGuid().ToString("D"),
                },
                default)).Value);
        Assert.IsType<GitSourceIdempotencyConflict>(
            (await Sources.RequestSyncAsync(
                request with
                {
                    ExpectedRevision = 2,
                },
                default)).Value);
    }

    [Fact]
    public async Task PolicyUpdateAdvancesRevisionAndFenceAndSchedulesReplacementWork()
    {
        var source = Assert.IsType<RepositorySource>((await Sources.CreateAsync(Request(), default)).Value);
        var outcome = await Sources.ChangePolicyAsync(
            new ChangeGitSourcePolicy(source.TenantId, source.Id, 1, new GitSyncPolicy(TimeSpan.FromMinutes(10))),
            default);
        var updated = Assert.IsType<RepositorySource>(outcome.Value);
        Assert.Equal(2, updated.Revision);
        Assert.Equal(source.Repository, updated.Repository);
        var state = await Sources.StateAsync(source.TenantId, source.Id, default);
        Assert.Equal(1, state.Fence);
        Assert.Equal(2, state.RequestedGeneration);
        Assert.Equal(1, state.CompletedGeneration);
        Assert.True(state.NextPollAtUtc > DateTime.UtcNow.AddMinutes(9));
        Assert.Equal(2, await WakeCount());
    }

    [Fact]
    public async Task BatchedSourceViewsKeepTenantIsolationAndPendingStatus()
    {
        var source = Assert.IsType<RepositorySource>((await Sources.CreateAsync(Request(), default)).Value);
        var queries = Services.GetRequiredService<GitRepositoryQueries>();
        var page = await queries.ListAsync(source.TenantId, 1, 50, default);
        var view = Assert.Single(page.Items);
        Assert.Equal(source.Id, view.Id);
        Assert.Equal(1, view.Synchronization.RequestedGeneration);
        Assert.Equal(0, view.Inventory.Owned);
        Assert.Null(await queries.SourceAsync("foreign", source.Id, default));
        Assert.Empty((await queries.ListAsync("foreign", 1, 50, default)).Items);
    }

    private CreateGitRepositorySource Request() => new(
        Data.TenantId,
        Guid.NewGuid().ToString("D"),
        new GitRepositoryLocation("Example", "connection", "repository", "main", ""),
        new GitSyncPolicy(TimeSpan.FromMinutes(5)),
        new GitSourceAudit(Data.UserId, DateTime.UtcNow),
        "https://example.test/repository");

    private Task<long> WakeCount() => DbContext.OutboxMessages.CountDocumentsAsync(item =>
        item.TenantId == Data.TenantId && item.EventType == "pole.gitops/v1/source-work-requested");
}
