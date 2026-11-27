// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Global3)]
public sealed class GitLifecycleExecutionTests(
    MongoReplicaSetFixture fixture
) : IsolatedMediatorIntegrationTestBase(fixture)
{
    private readonly Repository repository = new();
    private WidgetProvider provider = null!;
    private GitRepositorySources Sources => Services.GetRequiredService<GitRepositorySources>();
    private GitLifecyclePreviews Previews => Services.GetRequiredService<GitLifecyclePreviews>();

    private IMongoCollection<ManagedDeclaration> Inventory =>
        Services.GetRequiredService<IMongoCollection<ManagedDeclaration>>();

    private RepositorySourceJob Worker => Services.GetRequiredService<RepositorySourceJob>();

    protected override void ConfigureServices(IServiceCollection services)
    {
        provider = new WidgetProvider(Fixture.Database.GetCollection<BsonDocument>("LifecycleWidgets"));
        services.AddSingleton<IDeclarativeResourceProvider>(provider);
        services.AddSingleton<IRepositorySnapshotProvider>(repository);
    }

    [Fact]
    public async Task Offline_empty_detach_completes_without_any_commit_or_repository_provider()
    {
        var source = await Create("UnavailableProvider");
        var preview = await Preview(source, new GitPreviewDetachSource());
        var scheduled = await Confirm(source, preview);
        await Worker.ProcessAsync(new GitSourceIdentity(source.TenantId, source.Id), default);
        var retained = (await Sources.LoadAsync(source.TenantId, source.Id, default))!;
        Assert.Equal(GitSourceActivity.Detached, retained.Activity);
        var run = (await Sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        Assert.Null(run.Commit);
        Assert.Equal(GitRunCompletion.LifecycleCompleted, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.Null((await Sources.StateAsync(source.TenantId, source.Id, default)).Commits.FullyApplied);
    }

    [Fact]
    public async Task Detach_releases_pending_ownership_preserves_resource_and_returns_manual_control()
    {
        var source = await Create("UnavailableProvider");
        await Own(source);
        var before = await provider.Documents.Find(_ => true).SingleAsync();
        var preview = await Preview(source, new GitPreviewDetachSource());
        await Confirm(source, preview);
        await Worker.ProcessAsync(new GitSourceIdentity(source.TenantId, source.Id), default);
        Assert.Equal(before, await provider.Documents.Find(_ => true).SingleAsync());
        Assert.Equal(0, await Inventory.CountDocumentsAsync(_ => true));
        Assert.Equal(0, provider.DeletionRequests);
        var transactions = Services.GetRequiredService<MongoRunTransaction>();
        Assert.True(
            await transactions.InvokeOutcomeAsync<bool>(async (tx, ct) => tx.Commit(
                await Services.GetRequiredService<IGitOpsMutations>()
                    .AuthorizeAsync(
                        new AuthorizeGitResourceMutationRequest(source.TenantId, provider.Kind, "resource"),
                        tx.Session,
                        ct))));
    }

    [Fact]
    public async Task Inventory_growth_or_resource_change_invalidates_preview_without_admitting_work()
    {
        var source = await Create();
        await Own(source);
        var preview = await Preview(source, new GitPreviewDetachSource());
        await provider.Documents.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Inc("Revision", 1));
        Assert.IsType<GitPreviewStale>(
            (await Previews.ConfirmAsync(
                new ConfirmLifecyclePreview(
                    new GitSourceIdentity(source.TenantId, source.Id),
                    preview.Id,
                    preview.Revision,
                    Data.UserId),
                default)).Value);
        Assert.Equal(1, await Inventory.CountDocumentsAsync(_ => true));
        Assert.Equal(1, (await Sources.StateAsync(source.TenantId, source.Id, default)).RequestedGeneration);
        var fresh = await Preview(source, new GitPreviewDetachSource());
        await Services.GetRequiredService<IMongoCollection<GitSynchronizationState>>()
            .UpdateOneAsync(_ => true, Builders<GitSynchronizationState>.Update.Inc(item => item.InventoryRevision, 1));
        Assert.IsType<GitPreviewStale>(
            (await Previews.ConfirmAsync(
                new ConfirmLifecyclePreview(
                    new GitSourceIdentity(source.TenantId, source.Id),
                    fresh.Id,
                    fresh.Revision,
                    Data.UserId),
                default)).Value);
    }

    [Fact]
    public async Task Confirmation_is_idempotent_and_isolated_from_other_tenants()
    {
        var source = await Create("UnavailableProvider");
        var preview = await Preview(source, new GitPreviewDetachSource());
        var first = await Confirm(source, preview);
        var second = await Confirm(source, preview);
        Assert.Equal(first.RunId, second.RunId);
        Assert.Equal(
            1,
            await Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>().CountDocumentsAsync(_ => true));
        Assert.IsType<GitSourceNotFound>(
            (await Previews.ConfirmAsync(
                new ConfirmLifecyclePreview(
                    new GitSourceIdentity("other", source.Id),
                    preview.Id,
                    preview.Revision,
                    Data.UserId),
                default)).Value);
    }

    [Fact]
    public async Task Removal_waits_for_cleanup_then_recovers_without_another_delete_or_failure_budget()
    {
        var source = await Create();
        await Own(source);
        var preview = await Preview(
            source,
            new GitPreviewRemoval([new GitResourceIdentity(provider.Kind, "demo", "resource")]));
        var scheduled = await Confirm(source, preview);
        await Worker.ProcessAsync(new GitSourceIdentity(source.TenantId, source.Id), default);
        var run = (await Sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        Assert.IsType<GitRunWaitingCleanup>(run.State);
        Assert.Equal(0, run.ConsecutiveFailures);
        Assert.Equal(1, provider.DeletionRequests);
        Assert.Equal(1, await Inventory.CountDocumentsAsync(_ => true));
        await provider.Documents.DeleteOneAsync(_ => true);
        await Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>()
            .ReplaceOneAsync(
                item => item.Id == run.Id,
                run with { State = new GitRunWaitingCleanup(1, DateTime.UtcNow.AddMinutes(-1)) });
        await Worker.ProcessAsync(new GitSourceIdentity(source.TenantId, source.Id), default);
        var complete = (await Sources.RunAsync(source.TenantId, source.Id, run.Id, default))!;
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(complete.State).Completion);
        Assert.Equal(0, await Inventory.CountDocumentsAsync(_ => true));
        Assert.Equal(1, provider.DeletionRequests);
        Assert.Equal(
            new string('a', 40),
            (await Sources.StateAsync(source.TenantId, source.Id, default)).Commits.FullyApplied);
    }

    [Fact]
    public async Task Live_authority_only_allows_its_confirmed_effect_identity_and_commit()
    {
        var source = await Create();
        await Own(source);
        var preview = await Preview(
            source,
            new GitPreviewRemoval([new GitResourceIdentity(provider.Kind, "demo", "resource")]));
        var scheduled = await Confirm(source, preview);
        var claim = Assert.IsType<GitWorkClaim>(
            (await Services.GetRequiredService<GitRunStore>()
                .ClaimAsync(new GitSourceIdentity(source.TenantId, source.Id), "worker", default)).Value);
        var authority = new GitLifecycleAuthority(
            source.TenantId,
            source.Id,
            scheduled.RunId,
            claim.Run.Stamp.ConfigurationRevision,
            claim.Run.Stamp.Generation,
            claim.Run.Stamp.Fence,
            claim.Handle.Identity.WorkerId,
            claim.Handle.Identity.Attempt,
            preview.Id,
            Assert.Single(preview.Operations).Id,
            claim.Run.Commit);
        var mutations = Services.GetRequiredService<IGitOpsMutations>();
        var transactions = Services.GetRequiredService<MongoRunTransaction>();

        Task<bool> Authorized(
            GitLifecycleAuthority input,
            GitLifecycleMutationEffect? effect,
            string id = "resource") => transactions.InvokeOutcomeAsync<bool>(async (tx, ct) => tx.Commit(
            await mutations.AuthorizeAsync(
                new AuthorizeGitResourceMutationRequest(
                    source.TenantId,
                    provider.Kind,
                    id,
                    Lifecycle: input,
                    LifecycleEffect: effect),
                tx.Session,
                ct)));

        Assert.False(await Authorized(authority, null));
        Assert.False(await Authorized(authority, GitLifecycleMutationEffect.Adopt));
        Assert.False(await Authorized(authority, GitLifecycleMutationEffect.Remove, "other-resource"));
        Assert.False(
            await Authorized(authority with { Commit = new string('b', 40) }, GitLifecycleMutationEffect.Remove));
        Assert.True(await Authorized(authority, GitLifecycleMutationEffect.Remove));
        Assert.False((await provider.Documents.Find(_ => true).SingleAsync())["Deleting"].AsBoolean);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_head_or_revoked_access_stops_the_next_confirmed_removal(bool revoke)
    {
        var source = await Create();
        await Own(source);
        await Own(source, "other-resource", "other");
        var preview = await Preview(
            source,
            new GitPreviewRemoval(
            [
                new GitResourceIdentity(provider.Kind, "demo", "resource"),
                new GitResourceIdentity(provider.Kind, "other", "other-resource"),
            ]));
        var scheduled = await Confirm(source, preview);
        provider.OnRemovalRequested = () =>
        {
            if (revoke)
                repository.Granted = false;
            else
                repository.Commit = new string('b', 40);
        };
        await Worker.ProcessAsync(new GitSourceIdentity(source.TenantId, source.Id), default);

        Assert.Equal(1, provider.DeletionRequests);
        Assert.Equal(1, await provider.Documents.CountDocumentsAsync(new BsonDocument("Deleting", true)));
        Assert.Equal(1, await provider.Documents.CountDocumentsAsync(new BsonDocument("Deleting", false)));
        var run = (await Sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.Contains(
            run.Diagnostics,
            item => item.Code == (revoke ? "RepositoryAccessUnavailable" : nameof(GitLifecycleApprovalStale)));
    }

    [Fact]
    public async Task Preview_merges_sorted_distinct_owner_policy_codes_for_a_third_party_kind()
    {
        var source = await Create();
        await Own(source);
        provider.Protection = GitLifecycleProtection.ProtectedData;
        provider.PolicyWarnings = ["ProviderRetentionPolicy", "DataMayBeLost", "ProviderRetentionPolicy"];
        var before = await provider.Documents.Find(_ => true).SingleAsync();
        var preview = await Preview(
            source,
            new GitPreviewRemoval([new GitResourceIdentity(provider.Kind, "demo", "resource")]));
        Assert.Equal(
            new[] { "DataMayBeLost", "NoAutomaticDataMigration", "ProtectedData", "ProviderRetentionPolicy" },
            Assert.Single(preview.Operations).Warnings);
        Assert.Equal(
            new ReadLifecycleWarningsRequest(source.TenantId, "resource", GitLifecycleAction.Remove),
            provider.WarningRequest);
        Assert.Equal(before, await provider.Documents.Find(_ => true).SingleAsync());
        Assert.Equal(0, provider.DeletionRequests);
    }

    private async Task<RepositorySource> Create(string repositoryProvider = "Example") =>
        Assert.IsType<RepositorySource>(
            (await Sources.CreateAsync(
                new CreateGitRepositorySource(
                    Data.TenantId,
                    Guid.NewGuid().ToString("D"),
                    new GitRepositoryLocation(repositoryProvider, "connection", "repository", "main", ""),
                    new GitSyncPolicy(TimeSpan.FromMinutes(5)),
                    new GitSourceAudit(Data.UserId, DateTime.UtcNow),
                    "https://example.test/repository"),
                default)).Value);

    private async Task Own(
        RepositorySource source,
        string id = "resource",
        string name = "demo")
    {
        await provider.Documents.InsertOneAsync(
            new BsonDocument
            {
                { "_id", id }, { "TenantId", source.TenantId }, { "Name", name }, { "Revision", 1 },
                { "Deleting", false },
            });
        await Inventory.InsertOneAsync(
            new ManagedDeclaration(
                $"{source.TenantId}/Widget/{name}",
                source.TenantId,
                provider.Kind,
                name,
                source.Id,
                source.DisplayUrl,
                id,
                "desired",
                "applied",
                null,
                true,
                "old",
                "Prepare",
                0,
                GitResourceMaterialization.Created,
                GitManagedProjectionsFactory.Empty()));
        await Services.GetRequiredService<IMongoCollection<GitSynchronizationState>>()
            .UpdateOneAsync(_ => true, Builders<GitSynchronizationState>.Update.Inc(item => item.InventoryRevision, 1));
    }

    private async Task<GitOperationPreview> Preview(RepositorySource source, GitPreviewIntent intent) =>
        Assert.IsType<GitOperationPreview>(
            (await Previews.CreateAsync(
                new CreateLifecyclePreview(new GitSourceIdentity(source.TenantId, source.Id), source.Revision, intent),
                default)).Value);

    private async Task<GitLifecycleScheduled> Confirm(RepositorySource source, GitOperationPreview preview) =>
        Assert.IsType<GitLifecycleScheduled>(
            (await Previews.ConfirmAsync(
                new ConfirmLifecyclePreview(
                    new GitSourceIdentity(source.TenantId, source.Id),
                    preview.Id,
                    preview.Revision,
                    Data.UserId),
                default)).Value);

    private sealed class Repository : IRepositorySnapshotProvider
    {
        public string Commit { get; set; } = new('a', 40);
        public bool Granted { get; set; } = true;
        public string Provider => "Example";

        public Task<CheckRepositoryAccessOutcome> CheckAccessAsync(
            RepositorySourceReference repository,
            CancellationToken cancellationToken) => Task.FromResult<CheckRepositoryAccessOutcome>(
            Granted ? new RepositoryAccessGranted("https://example.test/repository") : new RepositoryAccessDenied());

        public Task<ResolveRepositoryCommitOutcome> ResolveCommitAsync(
            ResolveRepositoryCommitRequest request,
            CancellationToken cancellationToken) => Task.FromResult<ResolveRepositoryCommitOutcome>(
            new RepositoryCommitResolved(Commit));

        public Task<ReadRepositorySnapshotOutcome> ReadSnapshotAsync(
            ReadRepositorySnapshotRequest request,
            CancellationToken cancellationToken) => Task.FromResult<ReadRepositorySnapshotOutcome>(
            new RepositorySnapshotRead(request.Commit, []));
    }

    private sealed class WidgetProvider(
        IMongoCollection<BsonDocument> documents
    ) : IDeclarativeResourceProvider, IDeclarativeResourceLifecycleProvider, IDeclarativeLifecycleWarningProvider
    {
        public GitLifecycleProtection Protection { get; set; } = GitLifecycleProtection.ConfigurationOnly;
        public IReadOnlyList<string> PolicyWarnings { get; set; } = [];
        public ReadLifecycleWarningsRequest? WarningRequest { get; private set; }

        public int DeletionRequests { get; private set; }
        public Action? OnRemovalRequested { get; set; }
        public IMongoCollection<BsonDocument> Documents => documents;

        public Task<IReadOnlyList<string>> ReadLifecycleWarningsAsync(
            ReadLifecycleWarningsRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken)
        {
            WarningRequest = request;
            return Task.FromResult(PolicyWarnings);
        }

        public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
            ReadLifecycleResourceRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken)
        {
            var current = await documents.Find(
                    session,
                    new BsonDocument { { "_id", request.Resource.ResourceId }, { "TenantId", request.TenantId } })
                .FirstOrDefaultAsync(cancellationToken);
            return current is null
                ? new GitLifecycleResourceAbsent()
                : new GitLifecycleResourceSnapshot(
                    request.Resource,
                    new DeclarationVersionToken(
                    [
                        new DeclarationDocumentVersion(
                            $"Widget/{request.Resource.ResourceId}",
                            current["Revision"].ToInt64()),
                    ]),
                    current["Deleting"].AsBoolean ? GitLifecyclePresence.Deleting : GitLifecyclePresence.Active,
                    Protection,
                    GitLifecycleVersionSemantics.ObservedDocuments);
        }

        public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
            RemoveLifecycleResourceRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken)
        {
            var expected = request.Expected.Documents.Single().Revision;
            var changed = await documents.UpdateOneAsync(
                session,
                new BsonDocument
                {
                    { "_id", request.Resource.ResourceId }, { "TenantId", request.Authority.TenantId },
                    {
                        "Revision",
                        expected
                    },
                },
                Builders<BsonDocument>.Update.Set("Deleting", true).Inc("Revision", 1),
                cancellationToken: cancellationToken);
            if (changed.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
            DeletionRequests++;
            OnRemovalRequested?.Invoke();
            return new GitLifecycleRemovalRequested();
        }

        public string Kind => "Widget";
        public JsonElement NormalizeAndValidate(JsonElement spec) => spec;
        public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec) => [];

        public Task<string?> ResolveAsync(
            ResolveDeclaredResourceRequest request,
            IClientSessionHandle session,
            CancellationToken ct) => throw new InvalidOperationException("Lifecycle must use exact IDs.");

        public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct) =>
            throw new InvalidOperationException("Offline detach and removal cannot apply resource configuration.");
    }
}
