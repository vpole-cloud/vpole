using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Endpoints.Sources.Previews;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Planning;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class GitPrunePlanningTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private readonly Repository repository = new();
    private readonly WidgetProvider widgets =
        new("Widget", fixture.Database.GetCollection<BsonDocument>("PruneWidgets"));
    private readonly WidgetProvider gadgets =
        new("Gadget", fixture.Database.GetCollection<BsonDocument>("PruneGadgets"));
    private GitRepositorySources Sources => Services.GetRequiredService<GitRepositorySources>();
    private IMongoCollection<ManagedDeclaration> Inventory =>
        Services.GetRequiredService<IMongoCollection<ManagedDeclaration>>();
    private IMongoCollection<GitReconciliationRun> Runs =>
        Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>();
    private RepositorySourceJob Worker => Services.GetRequiredService<RepositorySourceJob>();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IRepositorySnapshotProvider>(repository);
        services.AddSingleton<IDeclarativeResourceProvider>(widgets);
        services.AddSingleton<IDeclarativeResourceProvider>(gadgets);
        services.AddSingleton<IDeclarativeLifecycleDependencyProvider>(widgets);
    }

    [Fact]
    public async Task Unprotected_prune_uses_policy_authority_in_the_current_run_and_reports_real_actions()
    {
        var source = await Create();
        await Own(source, widgets, "old", "old-id");
        await Own(source, gadgets, "kept", "kept-id");
        repository.Files = [Manifest("Gadget", "kept")];
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);

        var run = await Latest(source);
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(run.State).Completion);
        var plan = Assert.IsType<GitInputLifecycle>(run.Input).Plan;
        Assert.Equal(run.Id, Assert.IsType<GitPreviewPolicyAuthorized>(plan.Authorization).RunId);
        Assert.Equal(1, await Runs.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await widgets.Documents.CountDocumentsAsync(_ => true));
        Assert.Equal("kept-id", (await Inventory.Find(_ => true).SingleAsync()).ResourceId);
        Assert.Equal(1, repository.ResolveCalls);
        Assert.Equal(1, repository.ReadCalls);
        var queries = Services.GetRequiredService<GitRepositoryQueries>();
        var view = (await queries.RunAsync(source.TenantId, source.Id, run.Id, default))!;
        Assert.Equal(1, view.Operations!.NoOp);
        Assert.Equal(1, view.Operations.Lifecycle!.Remove);
        Assert.Equal(0, view.Operations.Pending);
        Assert.Equal(RepositoryLifecycleAuthorization.PrunePolicy, view.Lifecycle!.Authorization);
        var previewResponse = await new GetLifecyclePreviewHandler(
            Services.GetRequiredService<GitLifecyclePreviews>(), Sources).HandleAsync(
            new(source.Id, plan.PreviewId, source.TenantId), default);
        var previewView = Assert.IsType<LifecyclePreviewView>(previewResponse.Value);
        Assert.Equal("PolicyAuthorized", previewView.State);
        Assert.Equal(run.Id, previewView.RunId);
        var actions = (await queries.OperationsAsync(source.TenantId, source.Id, run.Id, 1, 100, default))!.Items;
        Assert.Null(Assert.Single(actions, item => item.Lifecycle is not null).Phase);
    }

    [Theory]
    [InlineData("protected")]
    [InlineData("empty")]
    [InlineData("ambiguous")]
    [InlineData("adoption")]
    [InlineData("replacement")]
    public async Task Dangerous_complete_inputs_wait_before_any_delete_or_create(string danger)
    {
        var source = await Create();
        widgets.Protection = danger == "protected" ? GitLifecycleProtection.ProtectedData :
            GitLifecycleProtection.ConfigurationOnly;
        await Own(source, widgets, "old", "old-id");
        if (danger is "protected" or "replacement")
            await Own(source, gadgets, "kept", "kept-id");
        if (danger == "adoption")
            await Insert(gadgets, source.TenantId, "manual", "manual-id");
        repository.Files = danger switch
        {
            "empty" => [],
            "ambiguous" => [Manifest("Widget", "new")],
            "adoption" => [Manifest("Gadget", "manual")],
            "replacement" => [Manifest("Gadget", "kept", "new-identity")],
            _ => [Manifest("Gadget", "kept")]
        };
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);

        var run = await Latest(source);
        var awaiting = Assert.IsType<GitInputAwaitingApproval>(run.Input);
        Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.Equal(1, await widgets.Documents.CountDocumentsAsync(_ => true));
        Assert.Equal(0, widgets.ApplyCalls + gadgets.ApplyCalls);
        var preview = await Services.GetRequiredService<IMongoCollection<GitOperationPreview>>()
            .Find(item => item.Id == awaiting.PreviewId).SingleAsync();
        Assert.IsType<GitPreviewAwaitingConfirmation>(preview.State);
        Assert.False(preview.CanAuthorizePrune());
        if (danger == "adoption")
            Assert.Equal("manual-id", Assert.Single(preview.Operations.Where(item =>
                item.Action == GitLifecycleAction.Adopt)).Resource.ResourceId);
        if (danger == "replacement")
            Assert.NotEqual("kept-id", Assert.Single(preview.Operations.Where(item =>
                item.Action == GitLifecycleAction.Replace)).Replacement!.ResourceId);
        var view = (await Services.GetRequiredService<GitRepositoryQueries>().SourceAsync(source.TenantId,
            source.Id, default))!;
        Assert.Equal(RepositoryRunStatus.AwaitingConfirmation, view.Status);
        Assert.Equal(awaiting.PreviewId, view.LatestRun!.Lifecycle!.PreviewId);
        Assert.True(view.LatestRun.Operations!.Lifecycle!.AwaitingConfirmation > 0);
        Assert.Null((await Sources.StateAsync(source.TenantId, source.Id, default)).Commits.FullyApplied);
    }

    [Fact]
    public async Task Incomplete_preview_blocks_without_pruning_a_previously_created_resource()
    {
        var source = await Create();
        await Own(source, widgets, "old", "old-id");
        await Own(source, gadgets, "kept", "kept-id");
        await gadgets.Documents.DeleteOneAsync(_ => true);
        repository.Files = [Manifest("Gadget", "kept")];
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);
        var run = await Latest(source);
        Assert.IsType<GitInputAccepted>(run.Input);
        Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.Equal(1, await widgets.Documents.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await Services.GetRequiredService<IMongoCollection<GitOperationPreview>>()
            .CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Protected_approval_becomes_stale_after_resource_change_and_retry_cannot_bypass_it()
    {
        var source = await Create();
        widgets.Protection = GitLifecycleProtection.ProtectedData;
        await Own(source, widgets, "old", "old-id");
        repository.Files = [];
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);
        var run = await Latest(source);
        var id = Assert.IsType<GitInputAwaitingApproval>(run.Input).PreviewId;
        await widgets.Documents.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Inc("Revision", 1));
        Assert.IsType<GitPreviewStale>((await Services.GetRequiredService<GitLifecyclePreviews>().ConfirmAsync(
            new(new(source.TenantId, source.Id), id, 1, TestData.UserId), default)).Value);
        Assert.IsType<GitRetryNotAvailable>((await Sources.RetryAsync(new(new(source.TenantId, source.Id,
            source.Revision, "retry", new(GitRunTrigger.Manual, TestData.UserId)), run.Id), default)).Value);
        Assert.Equal(1, await widgets.Documents.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Retain_relocation_records_retention_without_deletion_or_ownership_release()
    {
        var source = await Create(GitPrunePolicy.Retain);
        await Own(source, widgets, "old", "old-id");
        var inventoryRevision = (await Sources.StateAsync(source.TenantId, source.Id, default)).InventoryRevision;
        repository.DisplayUrl = "https://example.test/relocated";
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var relocation = new GitPreviewRelocateSource(source.Repository with { Branch = "next" });
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(
            new(new(source.TenantId, source.Id), source.Revision, relocation), default)).Value);
        Assert.Equal(GitLifecycleAction.Retain, Assert.Single(preview.Operations).Action);
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(new(
            new(source.TenantId, source.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);
        var run = (await Sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(run.State).Completion);
        var retained = await Inventory.Find(_ => true).SingleAsync();
        Assert.True(retained.PendingRemoval);
        Assert.Equal(repository.DisplayUrl, retained.SourceUrl);
        Assert.Equal(repository.DisplayUrl, (await Sources.LoadAsync(source.TenantId, source.Id, default))!.DisplayUrl);
        Assert.Equal(inventoryRevision,
            (await Sources.StateAsync(source.TenantId, source.Id, default)).InventoryRevision);
        Assert.Equal(1, await widgets.Documents.CountDocumentsAsync(_ => true));
        var receipt = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == run.Id).SingleAsync();
        Assert.Equal(GitLifecycleStep.Retain, receipt.Lifecycle!.Step);
        Assert.False(receipt.Changed);
    }

    [Fact]
    public async Task Policy_plan_survives_lease_takeover_without_resolving_a_new_branch_or_repeating_download()
    {
        var source = await Create();
        await Own(source, widgets, "old", "old-id");
        await Own(source, gadgets, "kept", "kept-id");
        var store = Services.GetRequiredService<GitRunStore>();
        var claim = Assert.IsType<GitWorkClaim>((await store.ClaimAsync(new(source.TenantId, source.Id), "first",
            default)).Value);
        var commit = new string('a', 40);
        Assert.True(await store.PinAsync(claim.Handle, commit, default));
        var pinned = claim.Run.Pin(commit);
        var manifest = Manifest("Gadget", "kept");
        var compiled = Services.GetRequiredService<ManifestCompiler>().Compile([new(manifest.Path, manifest.Content)]);
        var snapshot = await Services.GetRequiredService<GitSnapshotAcceptance>()
            .BuildAsync(claim with { Run = pinned }, compiled, default);
        Assert.True(await store.AcceptAsync(claim.Handle, snapshot, default));
        var result = await Services.GetRequiredService<GitPrunePlanning>()
            .PlanAsync(claim with { Run = pinned.Accept(snapshot) }, default);
        var authorized = Assert.IsType<GitPruneAuthorized>(result.Value).Claim.Run;
        await Runs.ReplaceOneAsync(item => item.Id == authorized.Id, authorized with
        {
            State = new GitRunClaimed(new("first", 1, DateTime.UtcNow.AddMinutes(-1)))
        });
        repository.Commit = new string('b', 40);
        await gadgets.Documents.UpdateOneAsync(_ => true,
            Builders<BsonDocument>.Update.Set("Value", "drift").Inc("Revision", 1));
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);
        var complete = await Latest(source);
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(complete.State).Completion);
        Assert.Equal(commit, complete.Commit);
        Assert.Equal(0, repository.ResolveCalls);
        Assert.Equal(0, repository.ReadCalls);
        Assert.Equal(0, await widgets.Documents.CountDocumentsAsync(_ => true));
        Assert.Equal(1, await Runs.CountDocumentsAsync(_ => true));
        Assert.Equal("value", (await gadgets.Documents.Find(_ => true).SingleAsync())["Value"].AsString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_fetch_or_compile_never_produces_deletion_authority(bool missingRepository)
    {
        var source = await Create();
        await Own(source, widgets, "old", "old-id");
        repository.Missing = missingRepository;
        repository.Files = [new("broken.yaml", "not: [valid")];
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);
        Assert.Equal(GitRunCompletion.Failed, Assert.IsType<GitRunFinished>((await Latest(source)).State).Completion);
        Assert.False((await Inventory.Find(_ => true).SingleAsync()).PendingRemoval);
        Assert.Equal(1, await widgets.Documents.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await Services.GetRequiredService<IMongoCollection<GitOperationPreview>>()
            .CountDocumentsAsync(_ => true));
    }

    private async Task<RepositorySource> Create(GitPrunePolicy prune = GitPrunePolicy.Prune)
    {
        widgets.Git = gadgets.Git = () => Services.GetRequiredService<IGitOpsMutations>();
        return Assert.IsType<RepositorySource>((await Sources.CreateAsync(new(TestData.TenantId,
            Guid.NewGuid().ToString("D"), new(repository.Provider, "connection", "repository", "main", ""),
            new(TimeSpan.FromMinutes(5)), new(TestData.UserId, DateTime.UtcNow), "https://example.test/repo", prune),
            default)).Value);
    }

    private async Task Own(RepositorySource source, WidgetProvider provider, string name, string id)
    {
        await Insert(provider, source.TenantId, name, id);
        await Inventory.InsertOneAsync(new($"{source.TenantId}/{provider.Kind}/{name}", source.TenantId, provider.Kind,
            name, source.Id, source.DisplayUrl, id, "old", "old", null, false, "previous", "Prepare", 0,
            GitResourceMaterialization.Created, GitManagedProjectionsFactory.Empty()));
        await Services.GetRequiredService<IMongoCollection<GitSynchronizationState>>().UpdateOneAsync(_ => true,
            Builders<GitSynchronizationState>.Update.Inc(item => item.InventoryRevision, 1));
    }

    private static Task Insert(WidgetProvider provider, string tenant, string name, string id) =>
        provider.Documents.InsertOneAsync(new BsonDocument
        {
            { "_id", id }, { "TenantId", tenant }, { "Name", name }, { "Revision", 1L },
            { "Identity", "identity" }, { "Value", "value" }, { "Fence", 0L }, { "Configured", true }
        });

    private async Task<GitReconciliationRun> Latest(RepositorySource source)
    {
        var state = await Sources.StateAsync(source.TenantId, source.Id, default);
        return (await Sources.RunAsync(source.TenantId, source.Id, state.LatestRunId!, default))!;
    }

    private static RepositoryManifestFile Manifest(string kind, string name, string identity = "identity",
        string value = "value") =>
        new($"{kind}-{name}.yaml", $"""
            apiVersion: {SnapshotSchema.ApiVersion}
            kind: {kind}
            metadata:
              name: {name}
            spec:
              identity: {identity}
              value: {value}
            """);

    private sealed class Repository : IRepositorySnapshotProvider
    {
        public string Provider => "PruneExample";
        public IReadOnlyList<RepositoryManifestFile> Files { get; set; } = [];
        public int ResolveCalls { get; private set; }
        public string Commit { get; set; } = new('a', 40);
        public bool Missing { get; set; }
        public string DisplayUrl { get; set; } = "https://example.test/repo";
        public int ReadCalls { get; private set; }
        public Task<CheckRepositoryAccessOutcome> CheckAccessAsync(RepositorySourceReference repository,
            CancellationToken cancellationToken) => Task.FromResult<CheckRepositoryAccessOutcome>(
            new RepositoryAccessGranted(DisplayUrl));
        public Task<ResolveRepositoryCommitOutcome> ResolveCommitAsync(ResolveRepositoryCommitRequest request,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult<ResolveRepositoryCommitOutcome>(new RepositoryCommitResolved(Commit));
        }
        public Task<ReadRepositorySnapshotOutcome> ReadSnapshotAsync(ReadRepositorySnapshotRequest request,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            return Task.FromResult<ReadRepositorySnapshotOutcome>(Missing ? new RepositoryNotFound() :
                new RepositorySnapshotRead(request.Commit, Files));
        }
    }

    private sealed record WidgetSpec(
        string Identity,
        string Value);

    private sealed class WidgetProvider(string kind, IMongoCollection<BsonDocument> documents)
        : ITransactionalDeclarativeResourceProvider, IDeclarativeResourceLifecycleProvider,
            IDeclarativeLifecycleDependencyProvider
    {
        public string Kind => kind;
        public string DependencyOwner => "TestWidgets";
        public IDeclarationFailure? LifecycleFailure { get; set; }
        public Func<IGitOpsMutations> Git { get; set; } = null!;
        public int ApplyCalls { get; private set; }
        public bool TwoPhase { get; set; }
        public Func<GitOperation, Task>? BeforeApply { get; set; }
        public Func<GitOperation, Task>? AfterApply { get; set; }
        public GitLifecycleProtection Protection { get; set; } = GitLifecycleProtection.ConfigurationOnly;
        public IMongoCollection<BsonDocument> Documents => documents;
        public JsonElement NormalizeAndValidate(JsonElement spec) =>
            DeclarationJson.Write(DeclarationJson.Read<WidgetSpec>(spec));
        public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec) =>
            TwoPhase ? [new(DeclarationPhase.Prepare, []),
                new(DeclarationPhase.Configure, [new(address, DeclarationPhase.Prepare)])]
                : [new(DeclarationPhase.Prepare, [])];
        public async Task<string?> ResolveAsync(ResolveDeclaredResourceRequest request, IClientSessionHandle session,
            CancellationToken ct) => (await documents.Find(session, new BsonDocument
            { { "TenantId", request.TenantId }, { "Name", request.Name } }).FirstOrDefaultAsync(ct))?["_id"].AsString;
        public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(ReadCurrentDeclarationRequest request,
            IClientSessionHandle session, CancellationToken ct)
        {
            var filter = new BsonDocument { { "TenantId", request.TenantId } };
            filter.Add(request.Operation is { } operation ? "_id" : "Name",
                request.Operation?.ResourceId ?? request.Address.Name);
            var found = await documents.Find(session, filter).FirstOrDefaultAsync(ct);
            return found is null ? new CurrentDeclarationMissing() : new CurrentDeclaration(found["_id"].AsString,
                Version(found), Project(new(found["Identity"].AsString, found["Value"].AsString)),
                found.GetValue("Configured", true).AsBoolean
                    ? DeclarationConfigurationState.Configured : DeclarationConfigurationState.Prepared);
        }
        public Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(ProjectDesiredDeclarationRequest request,
            IClientSessionHandle session, CancellationToken ct) => Task.FromResult<ProjectDesiredDeclarationOutcome>(
            Project(DeclarationJson.Read<WidgetSpec>(request.Declaration.Spec)));
        public async Task<FenceDeclarationOutcome> FenceAsync(FenceDeclarationRequest request,
            IClientSessionHandle session, CancellationToken ct)
        {
            var identity = new BsonDocument { { "_id", request.Operation.ResourceId },
                { "TenantId", request.Operation.Context.TenantId } };
            if (request.Expected.Documents.Count == 0)
                return await documents.Find(session, identity).AnyAsync(ct)
                    ? new DeclarationPreconditionsChanged() : new DeclarationFenced();
            identity.Add("Revision", request.Expected.Documents.Single().Revision);
            return (await documents.UpdateOneAsync(session, identity, Builders<BsonDocument>.Update.Inc("Fence", 1),
                cancellationToken: ct)).ModifiedCount == 1
                ? new DeclarationFenced() : new DeclarationPreconditionsChanged();
        }
        public async Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct)
        {
            ApplyCalls++;
            if (BeforeApply is not null)
                await BeforeApply(operation);
            var result = await Git().ExecuteAsync(operation, async (session, token) =>
            {
                var spec = DeclarationJson.Read<WidgetSpec>(operation.Declaration.Spec);
                var old = await documents.Find(session, new BsonDocument { { "_id", operation.ResourceId },
                    { "TenantId", operation.Context.TenantId } }).FirstOrDefaultAsync(token);
                await documents.ReplaceOneAsync(session, new BsonDocument { { "_id", operation.ResourceId },
                    { "TenantId", operation.Context.TenantId } }, new BsonDocument
                {
                    { "_id", operation.ResourceId }, { "TenantId", operation.Context.TenantId },
                    { "Name", operation.Declaration.Address.Name },
                    { "Revision", (old?.GetValue("Revision", 0L).ToInt64() ?? 0) + 1 }, { "Identity", spec.Identity },
                    { "Value", spec.Value }, { "Fence", 0L },
                    { "Configured", !TwoPhase || operation.Phase == DeclarationPhase.Configure }
                }, new ReplaceOptions { IsUpsert = true }, token);
                return new GitOperationResult(operation.ResourceId, Changed: true);
            }, ct);
            if (result.Applied && AfterApply is not null)
                await AfterApply(operation);
            return result;
        }
        public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(ReadLifecycleResourceRequest request,
            IClientSessionHandle session, CancellationToken ct)
        {
            if (LifecycleFailure is { } failure)
                return new DeclarationReadBlocked(failure);

            var found = await documents.Find(session, new BsonDocument { { "_id", request.Resource.ResourceId },
                { "TenantId", request.TenantId } }).FirstOrDefaultAsync(ct);
            return found is null ? new GitLifecycleResourceAbsent() : new GitLifecycleResourceSnapshot(request.Resource,
                Version(found), GitLifecyclePresence.Active, Protection,
                GitLifecycleVersionSemantics.ObservedDocuments);
        }
        public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(RemoveLifecycleResourceRequest request,
            IClientSessionHandle session, CancellationToken ct)
        {
            if (!await Git().AuthorizeAsync(new(request.Authority.TenantId, Kind, request.Resource.ResourceId,
                    Lifecycle: request.Authority, LifecycleEffect: GitLifecycleMutationEffect.Remove), session, ct))
                return new StaleGitExecution();
            return (await documents.DeleteOneAsync(session, new BsonDocument { { "_id", request.Resource.ResourceId },
                { "TenantId", request.Authority.TenantId },
                { "Revision", request.Expected.Documents.Single().Revision } },
                cancellationToken: ct)).DeletedCount == 1
                ? new GitLifecycleResourceRemoved() : new DeclarationPreconditionsChanged();
        }

        public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
            ReadLifecycleDependenciesRequest request, IClientSessionHandle session, CancellationToken ct)
        {
            if (request.Resource.Address.Kind != Kind)
                return [];

            var found = await documents.Find(session, new BsonDocument
            {
                { "TenantId", request.TenantId }, { "TargetResourceId", request.Resource.ResourceId }
            }).ToListAsync(ct);
            return found.Select(item => new GitLifecycleDependency(
                    new(new(Kind, item["Name"].AsString), item["_id"].AsString), request.Resource, Version(item)))
                .ToArray();
        }

        public Task<FenceDeclarationOutcome> FenceDependenciesAsync(FenceLifecycleDependenciesRequest request,
            IClientSessionHandle session, CancellationToken ct) =>
            request.Expected.Count == 0 ? Task.FromResult<FenceDeclarationOutcome>(new DeclarationFenced()) :
                throw new InvalidOperationException("Unowned test dependencies cannot be deleted implicitly.");

        private DeclarationVersionToken Version(BsonDocument document) => new(
            [new($"{Kind}/{document["_id"].AsString}", document["Revision"].ToInt64())]);
        private static DeclarationProjection Project(WidgetSpec spec) =>
            new(DeclarationJson.Write(new { spec.Identity }), DeclarationJson.Write(new { spec.Value }));
    }
}
