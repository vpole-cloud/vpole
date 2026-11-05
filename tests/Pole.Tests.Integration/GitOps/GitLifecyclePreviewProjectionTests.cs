using Pole.Features.GitOps.Compiler;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class GitLifecyclePreviewProjectionTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private readonly Repository repository = new();
    private readonly ProjectionProvider widgets = new("PreviewWidget", GitLifecycleProtection.ConfigurationOnly,
        fixture.Database.GetCollection<BsonDocument>("PreviewReviewWidgets"));
    private GitRepositorySources Sources => Services.GetRequiredService<GitRepositorySources>();
    private GitLifecyclePreviews Previews => Services.GetRequiredService<GitLifecyclePreviews>();
    private IMongoCollection<ManagedDeclaration> Inventory =>
        Services.GetRequiredService<IMongoCollection<ManagedDeclaration>>();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.RemoveAll<IDeclarativeResourceProvider>();
        services.AddSingleton<IDeclarativeResourceProvider>(widgets);
        foreach (var kind in new[] { "PostgresCluster", "RedisService", "Secret" })
            services.AddSingleton<IDeclarativeResourceProvider>(new ProjectionProvider(kind,
                GitLifecycleProtection.ProtectedData,
                Fixture.Database.GetCollection<BsonDocument>("PreviewReview" + kind)));
        services.AddSingleton<IRepositorySnapshotProvider>(repository);
    }

    [Fact]
    public async Task Relocation_discovers_immutable_replacement_before_changing_source_location()
    {
        var source = await Create();
        var original = await Own(source, widgets, "service");
        repository.Files = [Manifest(widgets.Kind, "service", "changed-identity")];
        var preview = await Relocate(source);
        var replacement = Assert.Single(preview.Operations);
        Assert.Equal(GitLifecycleAction.Replace, replacement.Action);
        Assert.Equal(original, replacement.Resource.ResourceId);
        Assert.NotEqual(original, replacement.Replacement!.ResourceId);
        Assert.Equal(source.Repository, (await Sources.LoadAsync(source.TenantId, source.Id, default))!.Repository);
        Assert.Equal(original, (await widgets.Documents.Find(_ => true).SingleAsync())["_id"].AsString);
        await ConfirmAndRun(source, preview);
        Assert.Equal(replacement.Replacement.ResourceId,
            (await widgets.Documents.Find(_ => true).SingleAsync())["_id"].AsString);
        Assert.Equal("destination", (await Sources.LoadAsync(source.TenantId, source.Id, default))!.Repository.Branch);
    }

    [Fact]
    public async Task Mutable_relocation_exposes_field_paths_and_keeps_the_accepted_resource_id()
    {
        var source = await Create();
        var original = await Own(source, widgets, "service");
        repository.Files = [Manifest(widgets.Kind, "service", value: "changed-value")];
        var preview = await Relocate(source);
        var apply = Assert.Single(preview.Operations);
        Assert.Equal(GitLifecycleAction.Apply, apply.Action);
        Assert.Equal(original, apply.Resource.ResourceId);
        Assert.Equal(new[] { "configuration.value" }, apply.ChangedFields);
        Assert.DoesNotContain("changed-value", JsonSerializer.Serialize(apply));
        await ConfirmAndRun(source, preview);
        var updated = await widgets.Documents.Find(_ => true).SingleAsync();
        Assert.Equal(original, updated["_id"].AsString);
        Assert.Equal("changed-value", updated["Value"].AsString);
        Assert.Equal(0, widgets.Deletions);
    }

    [Fact]
    public async Task Unchanged_relocation_keeps_identity_and_records_a_verified_no_op()
    {
        var source = await Create();
        var original = await Own(source, widgets, "service");
        repository.Files = [Manifest(widgets.Kind, "service")];
        var preview = await Relocate(source);
        var apply = Assert.Single(preview.Operations);
        Assert.Equal(GitLifecycleAction.Apply, apply.Action);
        Assert.Empty(apply.ChangedFields);
        await ConfirmAndRun(source, preview);
        var current = await widgets.Documents.Find(_ => true).SingleAsync();
        Assert.Equal(original, current["_id"].AsString);
        Assert.Equal(1, current["Revision"].ToInt64());
        Assert.Equal(0, widgets.ApplyCalls);
    }

    [Fact]
    public async Task Owned_projection_can_reference_a_reserved_dependency_before_it_is_prepared()
    {
        var source = await Create();
        var original = await Own(source, widgets, "service");
        repository.Files = [Manifest(widgets.Kind, "dependency"),
            Manifest(widgets.Kind, "service", dependency: "dependency")];
        var preview = await Relocate(source);
        Assert.True(widgets.SawPlannedReference);
        Assert.Equal(1, await widgets.Documents.CountDocumentsAsync(_ => true));
        Assert.Contains("configuration.dependencyId", Assert.Single(preview.Operations,
            item => item.Resource.ResourceId == original).ChangedFields);
        await ConfirmAndRun(source, preview);
        var dependency = await widgets.Documents.Find(new BsonDocument("Name", "dependency")).SingleAsync();
        var consumer = await widgets.Documents.Find(new BsonDocument("_id", original)).SingleAsync();
        Assert.Equal(dependency["_id"], consumer["DependencyId"]);
    }

    private async Task<RepositorySource> Create()
    {
        foreach (var provider in Services.GetServices<IDeclarativeResourceProvider>().Cast<ProjectionProvider>())
            provider.Git = Services.GetRequiredService<IGitOpsMutations>();
        return Assert.IsType<RepositorySource>((await Sources.CreateAsync(new(TestData.TenantId,
            Guid.NewGuid().ToString("D"), new(repository.Provider, "connection", "repository", "main", ""),
            new(TimeSpan.FromMinutes(5)), new(TestData.UserId, DateTime.UtcNow), "https://example.test/source"),
            default)).Value);
    }

    private async Task<string> Own(RepositorySource source, ProjectionProvider provider, string name,
        GitResourceMaterialization materialization = GitResourceMaterialization.Created)
    {
        var id = ObjectId.GenerateNewId().ToString();
        await provider.Documents.InsertOneAsync(Document(source.TenantId, id, name, "identity", "value", null, 1));
        await Inventory.InsertOneAsync(new($"{source.TenantId}/{provider.Kind}/{name}", source.TenantId, provider.Kind,
            name, source.Id, source.DisplayUrl, id, "old", "old", null, false, "previous", "Prepare", 0,
            materialization, GitManagedProjectionsFactory.Empty()));
        await Services.GetRequiredService<IMongoCollection<GitSynchronizationState>>().UpdateOneAsync(
            item => item.Source.SourceId == source.Id,
            Builders<GitSynchronizationState>.Update.Inc(item => item.InventoryRevision, 1));
        return id;
    }

    private async Task<GitOperationPreview> Relocate(RepositorySource source) => Assert.IsType<GitOperationPreview>(
        (await Previews.CreateAsync(new(new(source.TenantId, source.Id), source.Revision,
            new GitPreviewRelocateSource(source.Repository with { Branch = "destination" })), default)).Value);

    private async Task ConfirmAndRun(RepositorySource source, GitOperationPreview preview)
    {
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await Previews.ConfirmAsync(new(
            new(source.TenantId, source.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(source.TenantId, source.Id), default);
        var run = (await Sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(run.State).Completion);
    }

    private static RepositoryManifestFile Manifest(string kind, string name, string identity = "identity",
        string value = "value", string? dependency = null) => new(kind + "-" + name + ".yaml", $"""
        apiVersion: {SnapshotSchema.ApiVersion}
        kind: {kind}
        metadata:
          name: {name}
        spec:
          identity: {identity}
          value: {value}
          dependency: {(dependency is null ? "null" : dependency)}
        """);

    private static BsonDocument Document(string tenant, string id, string name, string identity, string value,
        string? dependency, long revision) => new()
    {
        { "_id", id }, { "TenantId", tenant }, { "Name", name }, { "Identity", identity }, { "Value", value },
        { "DependencyId", dependency is null ? BsonNull.Value : dependency }, { "Revision", revision }, { "Fence", 0L }
    };

    private sealed class Repository : IRepositorySnapshotProvider
    {
        public string Provider => "PreviewReview";
        public IReadOnlyList<RepositoryManifestFile> Files { get; set; } = [];
        public Task<CheckRepositoryAccessOutcome> CheckAccessAsync(RepositorySourceReference request,
            CancellationToken ct) => Task.FromResult<CheckRepositoryAccessOutcome>(
            new RepositoryAccessGranted("https://example.test/destination"));
        public Task<ResolveRepositoryCommitOutcome> ResolveCommitAsync(ResolveRepositoryCommitRequest request,
            CancellationToken ct) => Task.FromResult<ResolveRepositoryCommitOutcome>(
            new RepositoryCommitResolved(new string('a', 40)));
        public Task<ReadRepositorySnapshotOutcome> ReadSnapshotAsync(ReadRepositorySnapshotRequest request,
            CancellationToken ct) => Task.FromResult<ReadRepositorySnapshotOutcome>(new RepositorySnapshotRead(
            request.Commit, Files));
    }

    private sealed record Spec(
        string Identity,
        string Value,
        string? Dependency);

    private sealed class ProjectionProvider(string kind, GitLifecycleProtection protection,
        IMongoCollection<BsonDocument> documents) : ITransactionalDeclarativeResourceProvider,
        IDeclarativeResourceLifecycleProvider, IDeclarativeAdoptionPreviewProvider
    {
        public string Kind => kind;
        public IMongoCollection<BsonDocument> Documents => documents;
        public IGitOpsMutations Git { get; set; } = null!;
        public bool SawPlannedReference { get; private set; }
        public int ApplyCalls { get; private set; }
        public int Deletions { get; private set; }
        public JsonElement NormalizeAndValidate(JsonElement spec) =>
            DeclarationJson.Write(DeclarationJson.Read<Spec>(spec));
        public DeclarationFacts Facts(JsonElement spec) => DeclarationJson.Read<Spec>(spec).Dependency is { } name
            ? new(null, [], [new(new(Kind, name), "spec.dependency")], [], []) : new(null, [], [], [], []);
        public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec) =>
            [new(DeclarationPhase.Prepare, DeclarationJson.Read<Spec>(spec).Dependency is { } name
                ? [new(new(Kind, name), DeclarationPhase.Prepare)] : [])];
        public async Task<string?> ResolveAsync(ResolveDeclaredResourceRequest request, IClientSessionHandle session,
            CancellationToken ct) => (await documents.Find(session, ByName(request.TenantId, request.Name))
            .FirstOrDefaultAsync(ct))?["_id"].AsString;
        public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(ReadCurrentDeclarationRequest request,
            IClientSessionHandle session, CancellationToken ct)
        {
            var row = await documents.Find(session, request.Operation is { } operation
                ? ById(request.TenantId, operation.ResourceId) : ByName(request.TenantId, request.Address.Name))
                .FirstOrDefaultAsync(ct);
            return row is null ? new CurrentDeclarationMissing() : Current(row);
        }
        public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
            ProjectDesiredDeclarationRequest request,
            IClientSessionHandle session, CancellationToken ct)
        {
            var spec = DeclarationJson.Read<Spec>(request.Declaration.Spec);
            string? id = null;
            if (spec.Dependency is { } name)
                id = request.Operation is { } operation
                    ? await Git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId, new(Kind, name)),
                        session, ct)
                    : await ResolveAsync(new(request.TenantId, name), session, ct);
            return Projection(spec, id);
        }
        public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(PreviewLifecycleAdoptionRequest request,
            IClientSessionHandle session, CancellationToken ct)
        {
            var row = await documents.Find(session, ById(request.TenantId, request.ResourceId)).SingleAsync(ct);
            var spec = DeclarationJson.Read<Spec>(request.Declaration.Spec);
            var reference = spec.Dependency is null ? null : request.References.Single(item => item.Address ==
                new ResourceAddress(Kind, spec.Dependency));
            if (reference is not null && !await documents.Find(session, ById(request.TenantId, reference.ResourceId))
                    .AnyAsync(ct))
            {
                if (reference.State != GitPreviewReferenceState.Planned)
                    return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(reference.Address));
                SawPlannedReference = true;
            }
            return new GitLifecycleAdoptionPreview(Current(row), Projection(spec, reference?.ResourceId),
                request.References);
        }
        public async Task<FenceDeclarationOutcome> FenceAsync(FenceDeclarationRequest request,
            IClientSessionHandle session, CancellationToken ct)
        {
            var own = request.Expected.Documents.SingleOrDefault(item => item.DocumentId == Kind + "/" +
                request.Operation.ResourceId);
            var filter = ById(request.Operation.Context.TenantId, request.Operation.ResourceId);
            if (own is null)
                return await documents.Find(session, filter).AnyAsync(ct)
                    ? new DeclarationPreconditionsChanged() : new DeclarationFenced();
            filter.Add("Revision", own.Revision);
            return (await documents.UpdateOneAsync(session, filter, Builders<BsonDocument>.Update.Inc("Fence", 1),
                cancellationToken: ct)).ModifiedCount == 1
                ? new DeclarationFenced() : new DeclarationPreconditionsChanged();
        }
        public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct) =>
            Git.ExecuteAsync(operation, async (session, token) =>
            {
                ApplyCalls++;
                var spec = DeclarationJson.Read<Spec>(operation.Declaration.Spec);
                var dependency = spec.Dependency is null ? null : await Git.ResolveAcceptedAsync(new(operation.Context,
                    operation.ResourceId, new(Kind, spec.Dependency)), session, token);
                var old = await documents.Find(session, ById(operation.Context.TenantId, operation.ResourceId))
                    .FirstOrDefaultAsync(token);
                var updated = Document(operation.Context.TenantId, operation.ResourceId,
                    operation.Declaration.Address.Name, spec.Identity, spec.Value, dependency,
                    (old?["Revision"].ToInt64() ?? 0) + 1);
                updated["Fence"] = old?.GetValue("Fence", 0L) ?? 0L;
                await documents.ReplaceOneAsync(session, ById(operation.Context.TenantId, operation.ResourceId),
                    updated, new ReplaceOptions { IsUpsert = true }, token);
                return new(operation.ResourceId, Changed: true);
            }, ct);
        public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(ReadLifecycleResourceRequest request,
            IClientSessionHandle session, CancellationToken ct)
        {
            var row = await documents.Find(session, ById(request.TenantId, request.Resource.ResourceId))
                .FirstOrDefaultAsync(ct);
            return row is null ? new GitLifecycleResourceAbsent() : new GitLifecycleResourceSnapshot(request.Resource,
                Version(row), GitLifecyclePresence.Active, protection, GitLifecycleVersionSemantics.ObservedDocuments);
        }
        public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(RemoveLifecycleResourceRequest request,
            IClientSessionHandle session, CancellationToken ct)
        {
            if (!await Git.AuthorizeAsync(new(request.Authority.TenantId, Kind, request.Resource.ResourceId,
                    Lifecycle: request.Authority, LifecycleEffect: GitLifecycleMutationEffect.Remove), session, ct))
                return new StaleGitExecution();
            await documents.DeleteOneAsync(session, ById(request.Authority.TenantId, request.Resource.ResourceId),
                cancellationToken: ct);
            Deletions++;
            return new GitLifecycleResourceRemoved();
        }
        private DeclarationVersionToken Version(BsonDocument row) => new([new(Kind + "/" + row["_id"].AsString,
            row["Revision"].ToInt64())]);
        private CurrentDeclaration Current(BsonDocument row) => new(row["_id"].AsString, Version(row),
            Projection(new(row["Identity"].AsString, row["Value"].AsString, null),
                row["DependencyId"].IsBsonNull ? null : row["DependencyId"].AsString),
            DeclarationConfigurationState.Configured);
        private static DeclarationProjection Projection(Spec spec, string? dependency) => new(
            DeclarationJson.Write(new { identity = spec.Identity }),
            DeclarationJson.Write(new { value = spec.Value, dependencyId = dependency }));
        private static BsonDocument ById(string tenant, string id) => new() { { "TenantId", tenant }, { "_id", id } };
        private static BsonDocument ByName(string tenant, string name) =>
            new() { { "TenantId", tenant }, { "Name", name } };
    }
}
