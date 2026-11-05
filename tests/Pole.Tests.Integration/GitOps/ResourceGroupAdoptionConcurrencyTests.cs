using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Migrations;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed class ResourceGroupAdoptionConcurrencyTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private readonly TestRepositorySnapshots repository = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        NamedCatalogTestData.ConfigureIsolatedApi(services);
        services.AddSingleton<IRepositorySnapshotProvider>(repository);
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new GitOpsIndexesMigration().Up(Fixture.Database);
    }

    [Fact]
    public async Task Adoption_fences_a_manual_update_authorized_before_the_ownership_claim()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var seeded = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("manual-group"));
        var group = await DbContext.ResourceGroups.Find(item => item.Id == seeded.Id).SingleAsync(ct);
        var source = await ConfirmAdoptionAsync(group, ct);
        var mutations = new PausingMutations(Services.GetRequiredService<IGitOpsMutations>());
        var update = ActivatorUtilities.CreateInstance<MongoPersistUpdatedResourceGroup>(Services, mutations);
        var replacement = group.UpdateMetadata("racing manual update", group.Tags, DateTime.UtcNow);
        var changes = new ResourceGroupUpdatedEventChanges(
            UpdateLog<string>.Of(group.Description, replacement.Description),
            UpdateLog<ImmutableHashSet<string>>.Of(group.Tags, replacement.Tags));
        var eventType = EventTypeIdentity.GetRequiredName(typeof(ResourceGroupUpdatedEvent));
        var eventCount = await DbContext.CountOutboxMessagesAsync(eventType);
        var manual = update.InvokeAsync(group, replacement, changes, ct);
        await mutations.ManualAuthorized.Task.WaitAsync(ct);

        var snapshots = ActivatorUtilities.CreateInstance<GitAcceptedSnapshotExecutor>(Services, mutations);
        var lifecycle = ActivatorUtilities.CreateInstance<GitLifecycleExecutor>(Services, snapshots);
        var worker = ActivatorUtilities.CreateInstance<RepositorySourceJob>(Services, lifecycle);
        var execution = worker.ProcessAsync(new(source.TenantId, source.Id), ct);
        try
        {
            // Pause before the regular Apply/no-op fence: adoption itself must serialize manual writers.
            await mutations.ApplyReached.Task.WaitAsync(ct);
            var inventory = Services.GetRequiredService<IMongoCollection<ManagedDeclaration>>();
            Assert.True(await inventory.Find(item => item.SourceId == source.Id && item.ResourceId == group.Id)
                .AnyAsync(ct));
            mutations.ContinueManual.TrySetResult();
            var result = await manual.WaitAsync(ct);
            Assert.Equal(ResourceGroupRevisionConflictReason.GitOwnedConfiguration,
                Assert.IsType<ResourceGroupRevisionConflict>(result.Value).Reason);
            Assert.True(mutations.ManualAuthorizationAttempts >= 2,
                "Mongo must retry the conflicted manual transaction and recheck current ownership.");
        }
        finally
        {
            mutations.ContinueManual.TrySetResult();
            mutations.ContinueApply.TrySetResult();
            await Task.WhenAll(manual, execution).WaitAsync(ct);
        }

        var current = await DbContext.ResourceGroups.Find(item => item.Id == group.Id).SingleAsync(ct);
        Assert.Equal(group.Description, current.Description);
        Assert.Equal(group.Revision, current.Revision);
        Assert.Equal(group.UpdatedAtUtc, current.UpdatedAtUtc);
        Assert.Equal(eventCount,
            await DbContext.CountOutboxMessagesAsync(eventType));
        var state = await Services.GetRequiredService<GitRepositorySources>()
            .StateAsync(source.TenantId, source.Id, ct);
        var run = (await Services.GetRequiredService<GitRepositorySources>()
            .RunAsync(source.TenantId, source.Id, state.LatestRunId!, ct))!;
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(run.State).Completion);
        var receipts = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == run.Id).ToListAsync(ct);
        Assert.Single(receipts, item => item.Lifecycle?.Step == GitLifecycleStep.Adopt);
        Assert.Contains(receipts, item => item.Lifecycle?.Step == GitLifecycleStep.Apply);
    }

    private async Task<RepositorySource> ConfirmAdoptionAsync(ResourceGroup group, CancellationToken ct)
    {
        var repositoryId = ObjectId.GenerateNewId().ToString();
        repository.Publish(repositoryId,
        [
            new("group.yaml", JsonSerializer.Serialize(new
            {
                apiVersion = "pole.io/v1alpha1",
                kind = "ResourceGroup",
                metadata = new { name = group.Name },
                spec = new { description = group.Description, tags = group.Tags }
            }, DeclarationJson.Options))
        ]);
        var source = Assert.IsType<RepositorySource>((await Services.GetRequiredService<GitRepositorySources>()
            .CreateAsync(new(TestData.TenantId, Guid.NewGuid().ToString("D"),
                new(repository.Provider, TestRepositorySnapshots.ConnectionId, repositoryId, "main", ""),
                new(TimeSpan.FromMinutes(5)), new(TestData.UserId, DateTime.UtcNow),
                TestRepositorySnapshots.DisplayUrl), ct)).Value);
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(new(
            new(source.TenantId, source.Id), source.Revision,
            new GitPreviewAdoption([new("ResourceGroup", group.Name, group.Id)])), ct)).Value);
        Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(new(new(source.TenantId, source.Id),
            preview.Id, preview.Revision, TestData.UserId), ct)).Value);
        return source;
    }

    private sealed class PausingMutations(IGitOpsMutations inner) : DelegatingGitOpsMutations(inner)
    {
        private int manualAuthorizationAttempts;
        public int ManualAuthorizationAttempts => manualAuthorizationAttempts;
        public TaskCompletionSource ManualAuthorized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueManual { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ApplyReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueApply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<bool> AuthorizeAsync(AuthorizeGitResourceMutationRequest request,
            IClientSessionHandle session, CancellationToken ct)
        {
            var allowed = await base.AuthorizeAsync(request, session, ct);
            if (request.Context is null && request.Lifecycle is null &&
                Interlocked.Increment(ref manualAuthorizationAttempts) == 1)
            {
                Assert.True(allowed);
                ManualAuthorized.TrySetResult();
                await ContinueManual.Task.WaitAsync(ct);
            }
            return allowed;
        }

        public override async Task<GitOperationResult> ExecuteAsync(GitOperation operation,
            Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation, CancellationToken ct)
        {
            ApplyReached.TrySetResult();
            await ContinueApply.Task.WaitAsync(ct);
            return await base.ExecuteAsync(operation, mutation, ct);
        }
    }
}
