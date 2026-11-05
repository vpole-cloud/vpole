using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Migrations;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.FunctionApps;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed class GitLifecycleCleanupProofTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private IMongoCollection<GitLifecycleCleanupProof> Proofs =>
        Services.GetRequiredService<IMongoCollection<GitLifecycleCleanupProof>>();
    private GitRepositorySources Sources => Services.GetRequiredService<GitRepositorySources>();
    private IMongoCollection<GitReconciliationRun> Runs =>
        Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>();
    private IMongoCollection<ResourceGroup> Groups => Fixture.Database.GetCollection<ResourceGroup>("ResourceGroups");
    private IMongoCollection<IsolationScope> Scopes =>
        Fixture.Database.GetCollection<IsolationScope>("IsolationScopes");

    protected override void ConfigureServices(IServiceCollection services)
    {
        NamedCatalogTestData.ConfigureIsolatedApi(services);
        services.AddSingleton<IRepositorySnapshotProvider, EmptyRepository>();
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await NamedCatalogTestData.SeedAsync(Fixture);
        await new GitOpsIndexesMigration().Up(Fixture.Database);
        var profiles = Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles");
        var compute = await profiles.Find(item => item.Id == TestData.DefaultComputeProfileId).SingleAsync();
        compute = compute with { AllowedResourceTypes = compute.AllowedResourceTypes.Add("pole/function-app") };
        await profiles.ReplaceOneAsync(item => item.Id == compute.Id, compute);
        await Fixture.Database.GetCollection<ComputeProfileRevision>("ComputeProfileRevisions").ReplaceOneAsync(
            item => item.Id == $"{compute.Id}/1", ComputeProfileRevisionFactory.Create(compute));
        var targets = Fixture.Database.GetCollection<DeploymentTarget>("DeploymentTargets");
        var target = await targets.Find(item => item.Id == TestData.DefaultDeploymentTargetId).SingleAsync();
        await targets.ReplaceOneAsync(item => item.Id == target.Id, target with
        {
            WorkloadConfiguration = target.WorkloadConfiguration with
            {
                NetworkSources = new(ImmutableDictionary<string, DeploymentTargetNetworkSource>.Empty.Add(
                    FunctionResourceDeployment.GatewaySourceName,
                    DeploymentTargetCidrNetworkSource.Create("192.0.2.9/32")))
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Async_child_finalization_advances_its_own_parent_fences_but_never_an_external_gap(bool external)
    {
        var context = await StartRemoval(2);
        var before = await Groups.Find(item => item.Id == context.Group.Id).SingleAsync();
        if (external)
            await Groups.UpdateOneAsync(item => item.Id == before.Id,
                Builders<ResourceGroup>.Update.Inc(item => item.RegistrationFence, 1));
        foreach (var child in context.Children)
            await FinalizeChild(child);
        var proofs = await Proofs.Find(_ => true).ToListAsync();
        Assert.Equal(2, proofs.Count);
        Assert.All(proofs, proof => Assert.Equal(context.RunId, proof.RunId));
        Assert.Equal(before.RegistrationFence + 2 + (external ? 1 : 0),
            (await Groups.Find(item => item.Id == before.Id).SingleAsync()).RegistrationFence);

        await Resume(context);
        var run = await Runs.Find(item => item.Id == context.RunId).SingleAsync();
        if (external)
        {
            Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(run.State).Completion);
            Assert.Equal(nameof(GitLifecycleApprovalStale), Assert.Single(run.Diagnostics).Code);
            Assert.IsType<ActiveIsolationScopeLifecycleState>((await Scopes.Find(item => item.Id == context.Scope.Id)
                .SingleAsync()).Lifecycle);
            return;
        }

        Assert.IsType<GitRunWaitingCleanup>(run.State);
        var scope = await Scopes.Find(item => item.Id == context.Scope.Id).SingleAsync();
        Assert.IsType<DeletingIsolationScopeLifecycleState>(scope.Lifecycle);
        var cells = await DbContext.RuntimeCells.Find(item => item.IsolationScopeId == scope.Id).ToListAsync();
        foreach (var cell in cells)
            await DbContext.RuntimeCells.ReplaceOneAsync(item => item.Id == cell.Id,
                cell.MarkDeleted(cell.DesiredGeneration, cell.UpdatedAtUtc.AddSeconds(1)));
        await Services.GetRequiredService<FinalizeResourceGroupDeletion>()(context.Source.TenantId,
            context.Group.Id, scope.Id);
        Assert.Equal(3, await Proofs.CountDocumentsAsync(_ => true));
        await Resume(context);
        run = await Runs.Find(item => item.Id == context.RunId).SingleAsync();
        Assert.NotEqual(GitRunCompletion.Blocked, (run.State as GitRunFinished)?.Completion);
        Assert.False(await Groups.Find(item => item.Id == context.Group.Id).AnyAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_scope_cleanup_proves_its_group_action_and_preserves_external_revision_gaps(bool external)
    {
        var context = await StartRemoval(1, defaultScope: true);
        Assert.True(context.Scope.IsDefault);
        await FinalizeChild(context.Children[0]);
        await Resume(context);
        var cells = await DbContext.RuntimeCells.Find(item => item.IsolationScopeId == context.Scope.Id).ToListAsync();
        Assert.Equal(2, cells.Count);
        Assert.IsType<DeletingResourceGroupLifecycleState>((await Groups.Find(item => item.Id == context.Group.Id)
            .SingleAsync()).Lifecycle);
        await DbContext.RuntimeCells.ReplaceOneAsync(item => item.Id == cells[0].Id,
            cells[0].MarkDeleted(cells[0].DesiredGeneration, cells[0].UpdatedAtUtc.AddSeconds(1)));
        if (external)
            await Groups.UpdateOneAsync(item => item.Id == context.Group.Id,
                Builders<ResourceGroup>.Update.Inc(item => item.Revision, 1));
        await Services.GetRequiredService<FinalizeResourceGroupDeletion>()(context.Source.TenantId,
            context.Group.Id, context.Scope.Id);
        var proof = Assert.Single(await Proofs.Find(item => item.Subject.Kind == "ResourceGroup").ToListAsync());
        Assert.Equal(context.Group.Id, proof.Subject.ResourceId);
        Assert.Equal(context.RunId, proof.RunId);
        await Resume(context);
        var run = await Runs.Find(item => item.Id == context.RunId).SingleAsync();
        if (external)
        {
            Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(run.State).Completion);
            Assert.Equal(nameof(GitLifecycleApprovalStale), Assert.Single(run.Diagnostics).Code);
            Assert.True(await Groups.Find(item => item.Id == context.Group.Id).AnyAsync());
            return;
        }
        Assert.IsType<GitRunWaitingCleanup>(run.State);
        await DbContext.RuntimeCells.ReplaceOneAsync(item => item.Id == cells[1].Id,
            cells[1].MarkDeleted(cells[1].DesiredGeneration, cells[1].UpdatedAtUtc.AddSeconds(1)));
        await Services.GetRequiredService<FinalizeResourceGroupDeletion>()(context.Source.TenantId,
            context.Group.Id, context.Scope.Id);
        await Resume(context);
        run = await Runs.Find(item => item.Id == context.RunId).SingleAsync();
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.False(await Groups.Find(item => item.Id == context.Group.Id).AnyAsync());
        Assert.False(await Scopes.Find(item => item.Id == context.Scope.Id).AnyAsync());
    }

    [Fact]
    public async Task Runtime_delete_progress_can_resume_before_the_owner_finalizer_runs()
    {
        var context = await StartRemoval(1);
        var child = context.Children[0];
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == child.Id).SingleAsync();
        var at = placement.UpdatedAtUtc.AddSeconds(1);
        var claim = ObjectId.GenerateNewId().ToString();
        var started = placement.Start(placement.DesiredGeneration, ResourceDeploymentOperation.Delete,
            claim, at, at.AddMinutes(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id, started);
        await Resume(context);
        Assert.IsType<GitRunWaitingCleanup>((await Runs.Find(item => item.Id == context.RunId).SingleAsync()).State);
        var completed = started.Complete(started.DesiredGeneration, ResourceDeploymentOperation.Delete,
            claim, [], at.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id, completed);
        await Resume(context);
        Assert.IsType<GitRunWaitingCleanup>((await Runs.Find(item => item.Id == context.RunId).SingleAsync()).State);
        Assert.True(await DbContext.FunctionApps.Find(item => item.Id == child.Id).AnyAsync());
        Assert.Equal(0, await Proofs.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Registrar_proof_rolls_back_with_registration_and_parent_fence()
    {
        var context = await StartRemoval(1);
        var before = await Groups.Find(item => item.Id == context.Group.Id).SingleAsync();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<ResourceUnregistered>((await Services.GetRequiredService<IResourceRegistrar>().UnregisterAsync(
            new(context.Source.TenantId, context.Group.Id, FunctionResourceDeployment.ResourceType,
                context.Children[0].Id), session)).Value);
        Assert.Equal(1, await Proofs.CountDocumentsAsync(session, _ => true));
        await session.AbortTransactionAsync();
        Assert.Equal(0, await Proofs.CountDocumentsAsync(_ => true));
        Assert.Equal(before.RegistrationFence,
            (await Groups.Find(item => item.Id == before.Id).SingleAsync()).RegistrationFence);
        Assert.True(await DbContext.ResourceRegistrations.Find(item => item.ResourceId == context.Children[0].Id)
            .AnyAsync());
    }

    [Fact]
    public async Task Accepted_cleanup_records_proof_after_pause_without_reauthorizing_the_old_run()
    {
        var context = await StartRemoval(1);
        await Services.GetRequiredService<GitSourceLifecycleStore>().PauseAsync(new(
            context.Source.TenantId, context.Source.Id, context.Source.Revision, TestData.UserId), default);
        var state = await Sources.StateAsync(context.Source.TenantId, context.Source.Id, default);
        await FinalizeChild(context.Children[0]);
        Assert.Equal(context.RunId, (await Proofs.Find(_ => true).SingleAsync()).RunId);
        Assert.Equal(state.ToBsonDocument(),
            (await Sources.StateAsync(context.Source.TenantId, context.Source.Id, default)).ToBsonDocument());
    }

    [Fact]
    public async Task Declared_cleanup_records_exact_causal_proof_without_constructing_resource_providers()
    {
        var context = await StartRemoval(1);
        using var isolated = new ServiceCollection().BuildServiceProvider();
        var recorder = new GitLifecycleCleanupProofs(Proofs,
            Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>(), Runs,
            Services.GetRequiredService<IMongoCollection<ManagedDeclaration>>(), isolated);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var before = await Groups.Find(session, item => item.Id == context.Group.Id).SingleAsync();
        await Groups.UpdateOneAsync(session, item => item.Id == before.Id,
            Builders<ResourceGroup>.Update.Inc(item => item.RegistrationFence, 1));
        var child = context.Children[0];
        var transition = new GitCleanupVersionTransition($"ResourceGroup/{before.Id}/RegistrationFence",
            before.RegistrationFence, checked(before.RegistrationFence + 1));
        await recorder.RecordAsync(new(context.Source.TenantId, new GitDeclaredCleanupResource("FunctionApp", child.Id),
            "owner-cleanup", [transition]), session, default);
        await session.CommitTransactionAsync();

        var proof = await Proofs.Find(item => item.CleanupId == "owner-cleanup").SingleAsync();
        Assert.Equal(context.RunId, proof.RunId);
        Assert.Equal(new GitResourceIdentity("FunctionApp", child.NormalizedName, child.Id), proof.Subject);
        Assert.Equal(new GitCleanupDocumentTransition(transition.DocumentId, transition.Before, transition.After),
            Assert.Single(proof.Transitions));
    }

    private async Task<RemovalContext> StartRemoval(int count, bool defaultScope = false)
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var scope = defaultScope
            ? await Scopes.Find(item => item.Id == group.DefaultIsolationScopeId).SingleAsync()
            : IsolationScopeFactory.CreateAdditional(TestData.TenantId, group.Id, "reviewed-scope",
                IsolationScopeFactory.DefaultOptions);
        if (!defaultScope)
            await Scopes.InsertOneAsync(scope);
        var children = new List<FunctionApp>();
        for (var i = 0; i < count; i++)
        {
            var name = "child-" + i;
            var response = (await Mediator.Send(new CreateFunctionAppRequest(new(name, name, group.Id,
                new(new("registry.example", "function", "v1"),
                    new(ImmutableDictionary<string, string>.Empty,
                        ImmutableDictionary<string, FunctionSecretReference>.Empty),
                    new(new(100, 128), new(500, 512)), new(0, 1, 1, 2, 60, 30),
                    [new("http", 1, new HttpFunctionTriggerRequest("hello", ["GET"], FunctionExposure.Internal))]), [],
                Placement: new(TestData.DefaultDeploymentTargetId, scope.Id)), TestData.TenantId))).RequireSuccess();
            children.Add(await DbContext.FunctionApps.Find(item => item.Id == response.FunctionAppId).SingleAsync());
        }
        if (defaultScope)
        {
            var retainedTarget = TestData.SeedDeploymentTarget(ObjectId.GenerateNewId().ToString());
            await Fixture.Database.GetCollection<DeploymentTarget>("DeploymentTargets").InsertOneAsync(retainedTarget);
            await DbContext.RuntimeCells.InsertOneAsync(RuntimeCellFactory.Create(TestData.TenantId, group.Id,
                scope.Id, retainedTarget.Id, "retained-empty-cell", RuntimeCellFactory.DefaultOptions));
        }
        var source = Assert.IsType<RepositorySource>((await Sources.CreateAsync(new(TestData.TenantId,
            Guid.NewGuid().ToString("D"), new("Example", "connection", "repository", "main", ""),
            new(TimeSpan.FromMinutes(5)), new(TestData.UserId, DateTime.UtcNow), "https://example.test/repository"),
            default)).Value);
        GitResourceIdentity[] resources =
        [
            new("ResourceGroup", group.Name, group.Id),
            .. scope.IsDefault ? [] : new GitResourceIdentity[] { new("IsolationScope", scope.Name, scope.Id) },
            .. children.Select(item => new GitResourceIdentity("FunctionApp", item.NormalizedName, item.Id))
        ];
        var inventory = Services.GetRequiredService<IMongoCollection<ManagedDeclaration>>();
        foreach (var resource in resources)
            await inventory.InsertOneAsync(new($"{source.TenantId}/{resource.Kind}/{resource.Name}", source.TenantId,
                resource.Kind, resource.Name, source.Id, source.DisplayUrl, resource.ResourceId, "desired", "applied",
                null, true, "old", "Configure", 0, GitResourceMaterialization.Created,
                GitManagedProjectionsFactory.Empty()));
        await Services.GetRequiredService<IMongoCollection<GitSynchronizationState>>().UpdateOneAsync(_ => true,
            Builders<GitSynchronizationState>.Update.Inc(item => item.InventoryRevision, resources.Length));
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var request = new CreateLifecyclePreview(new(source.TenantId, source.Id),
            source.Revision, new GitPreviewRemoval(resources.ToImmutableArray()));
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(request, default)).Value);
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(new(new(source.TenantId,
            source.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(source.TenantId, source.Id), default);
        var run = await Runs.Find(item => item.Id == scheduled.RunId).SingleAsync();
        Assert.IsType<GitRunWaitingCleanup>(run.State);
        return new(source, group, scope, children, run.Id);
    }

    private async Task FinalizeChild(FunctionApp child)
    {
        var deployment = await DbContext.ResourceDeployments
            .Find(item => item.Id == child.Placement.ResourceDeploymentId)
            .SingleAsync();
        var at = deployment.UpdatedAtUtc.AddSeconds(1);
        var claim = ObjectId.GenerateNewId().ToString();
        var deleted = deployment.Start(deployment.DesiredGeneration, ResourceDeploymentOperation.Delete,
                claim, at, at.AddMinutes(1))
            .Complete(deployment.DesiredGeneration, ResourceDeploymentOperation.Delete, claim, [], at.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deleted.Id, deleted);
        Assert.IsType<FunctionAppDeletionFinalized>((await Services.GetRequiredService<FinalizeFunctionAppDeletion>()(
            child.TenantId, child.Id, child.Placement.ResourceDeploymentId)).Value);
        Assert.False(await DbContext.FunctionApps.Find(item => item.Id == child.Id).AnyAsync());
    }

    private async Task Resume(RemovalContext context)
    {
        var run = await Runs.Find(item => item.Id == context.RunId).SingleAsync();
        var waiting = Assert.IsType<GitRunWaitingCleanup>(run.State);
        await Runs.ReplaceOneAsync(item => item.Id == run.Id,
            run with { State = new GitRunWaitingCleanup(waiting.LastAttempt, DateTime.UtcNow.AddMinutes(-1)) });
        using var restarted = Services.CreateScope();
        await restarted.ServiceProvider.GetRequiredService<RepositorySourceJob>()
            .ProcessAsync(new(context.Source.TenantId, context.Source.Id), default);
    }

    private sealed record RemovalContext(
        RepositorySource Source,
        ResourceGroup Group,
        IsolationScope Scope,
        IReadOnlyList<FunctionApp> Children,
        string RunId);

    private sealed class EmptyRepository : IRepositorySnapshotProvider
    {
        public string Provider => "Example";
        public Task<CheckRepositoryAccessOutcome> CheckAccessAsync(RepositorySourceReference repository,
            CancellationToken cancellationToken) => Task.FromResult<CheckRepositoryAccessOutcome>(
            new RepositoryAccessGranted("https://example.test/repository"));
        public Task<ResolveRepositoryCommitOutcome> ResolveCommitAsync(ResolveRepositoryCommitRequest request,
            CancellationToken cancellationToken) => Task.FromResult<ResolveRepositoryCommitOutcome>(
            new RepositoryCommitResolved(new string('a', 40)));
        public Task<ReadRepositorySnapshotOutcome> ReadSnapshotAsync(ReadRepositorySnapshotRequest request,
            CancellationToken cancellationToken) => Task.FromResult<ReadRepositorySnapshotOutcome>(
            new RepositorySnapshotRead(request.Commit, []));
    }
}
