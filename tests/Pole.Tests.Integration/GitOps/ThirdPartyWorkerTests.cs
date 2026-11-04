using Pole.Features.GitOps.Migrations;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed class ThirdPartyWorkerTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private readonly TestRepositorySnapshots repository = new();
    private readonly WidgetBehavior behavior = new();
    private readonly string source = ObjectId.GenerateNewId().ToString();
    private IMongoCollection<TestWidget> Widgets => Fixture.Database.GetCollection<TestWidget>("TestWidgets");
    private RepositoryPipelineDriver Pipeline => new(Services, Fixture, repository);

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IRepositorySnapshotProvider>(repository);
        services.AddScoped<IDeclarativeResourceProvider>(provider => new WidgetProvider(Widgets,
            provider.GetRequiredService<IGitOpsMutations>(), behavior));
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new GitOpsIndexesMigration().Up(Fixture.Database);
    }

    private Task<RepositoryPipelineResult> Apply() => Pipeline.SubmitAsync(new(TestData.TenantId, source),
        [new("widget.yaml", """
            apiVersion: pole.io/v1alpha1
            kind: ThirdPartyWidget
            metadata:
              name: widget
            spec:
              value: 1
            """)]);

    [Fact]
    public async Task ExternalProviderCreatesRepairsAndNoOpsWithoutEngineChanges()
    {
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply()).Status);
        var widget = await Widgets.Find(Builders<TestWidget>.Filter.Empty).SingleAsync();
        await Widgets.UpdateOneAsync(item => item.Id == widget.Id,
            Builders<TestWidget>.Update.Set(item => item.Value, 2).Inc(item => item.Revision, 1));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply()).Status);
        Assert.Equal(1, (await Widgets.Find(item => item.Id == widget.Id).SingleAsync()).Value);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply()).Status);
        Assert.Equal(GitRunCompletion.AlreadyCurrent,
            Assert.IsType<GitRunFinished>((await Pipeline.LatestAsync(source)).State).Completion);
        Assert.Equal(2, behavior.ApplyCalls);
    }

    [Fact]
    public async Task ThreeImmediatePreconditionConflictsPersistBackoffAndRecoveryReplans()
    {
        await Apply();
        var widget = await Widgets.Find(Builders<TestWidget>.Filter.Empty).SingleAsync();
        await Widgets.UpdateOneAsync(item => item.Id == widget.Id,
            Builders<TestWidget>.Update.Set(item => item.Value, 2).Inc(item => item.Revision, 1));
        behavior.ConflictsRemaining = 3;
        var result = await Apply();
        Assert.Equal(RepositoryPipelineStatus.Applying, result.Status);
        Assert.Equal(4, behavior.ApplyCalls);
        var delayed = await Pipeline.LatestAsync(source);
        Assert.IsType<GitRunDelayed>(delayed.State);
        Assert.Equal(1, delayed.ConsecutiveFailures);
        Assert.Equal(2, (await Widgets.Find(item => item.Id == widget.Id).SingleAsync()).Value);
        var plan = await Services.GetRequiredService<IMongoCollection<GitPlannedOperation>>()
            .Find(item => item.Stamp.Run.RunId == delayed.Id).SingleAsync();
        Assert.Equal(3, plan.Revision);
        await Pipeline.MakeRecoveryDueAsync(source);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied,
            (await Pipeline.RecoverAsync(new(TestData.TenantId, source))).Status);
        Assert.Equal(1, (await Widgets.Find(item => item.Id == widget.Id).SingleAsync()).Value);
        Assert.Equal(5, behavior.ApplyCalls);
    }

    [Fact]
    public async Task IncorrectProviderSuccessRollsBackResourceAndDoesNotMarkInventoryCreated()
    {
        behavior.WriteIncorrectValue = true;
        var result = await Apply();
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(0, await Widgets.CountDocumentsAsync(Builders<TestWidget>.Filter.Empty));
        var inventory = await Services.GetRequiredService<IMongoCollection<ManagedDeclaration>>()
            .Find(item => item.SourceId == source).SingleAsync();
        Assert.Equal(GitResourceMaterialization.Allocated, inventory.Materialization);
        Assert.Null(inventory.Projections.Applied);
        var receipt = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == result.Context!.RunId).SingleAsync();
        Assert.Equal(nameof(DeclarationPostconditionFailed), receipt.Blocker);
    }

    private sealed record WidgetSpec(int Value);

    private sealed record TestWidget(
        [property: BsonId] string Id,
        string TenantId,
        string Name,
        int Value,
        long Revision,
        long Fence);

    private sealed class WidgetBehavior
    {
        public int ApplyCalls { get; set; }
        public int ConflictsRemaining { get; set; }
        public bool WriteIncorrectValue { get; set; }
    }

    private sealed class WidgetProvider(IMongoCollection<TestWidget> widgets, IGitOpsMutations git,
        WidgetBehavior behavior) : ITransactionalDeclarativeResourceProvider
    {
        public string Kind => "ThirdPartyWidget";
        public JsonElement NormalizeAndValidate(JsonElement spec) =>
            DeclarationJson.Write(DeclarationJson.Read<WidgetSpec>(spec));
        public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec) =>
            [new(DeclarationPhase.Prepare, [])];
        public async Task<string?> ResolveAsync(ResolveDeclaredResourceRequest request,
            IClientSessionHandle session, CancellationToken ct) =>
            (await widgets.Find(session, item => item.TenantId == request.TenantId && item.Name == request.Name)
                .FirstOrDefaultAsync(ct))?.Id;
        public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(ReadCurrentDeclarationRequest request,
            IClientSessionHandle session, CancellationToken cancellationToken)
        {
            var id = request.Operation!.ResourceId;
            var current = await widgets.Find(session, item => item.TenantId == request.TenantId && item.Id == id)
                .FirstOrDefaultAsync(cancellationToken);
            return current is null ? new CurrentDeclarationMissing() : new CurrentDeclaration(current.Id,
                new([new(current.Id, current.Revision)]), Projection(current.Value),
                DeclarationConfigurationState.Configured);
        }
        public Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(ProjectDesiredDeclarationRequest request,
            IClientSessionHandle session, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectDesiredDeclarationOutcome>(Projection(
                DeclarationJson.Read<WidgetSpec>(request.Declaration.Spec).Value));
        public async Task<FenceDeclarationOutcome> FenceAsync(FenceDeclarationRequest request,
            IClientSessionHandle session, CancellationToken cancellationToken)
        {
            var expected = request.Expected.Documents.SingleOrDefault();
            if (expected is null)
                return new DeclarationFenced();
            var result = await widgets.UpdateOneAsync(session, item =>
                    item.TenantId == request.Operation.Context.TenantId && item.Id == request.Operation.ResourceId &&
                    item.Revision == expected.Revision,
                Builders<TestWidget>.Update.Inc(item => item.Fence, 1), cancellationToken: cancellationToken);
            return result.ModifiedCount == 1 ? new DeclarationFenced() : new DeclarationPreconditionsChanged();
        }
        public async Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct)
        {
            behavior.ApplyCalls++;
            if (behavior.ConflictsRemaining > 0)
            {
                behavior.ConflictsRemaining--;
                await widgets.UpdateOneAsync(item => item.Id == operation.ResourceId,
                    Builders<TestWidget>.Update.Inc(item => item.Revision, 1), cancellationToken: ct);
            }
            return await git.ExecuteAsync(operation, async (session, token) =>
            {
                var current = await widgets.Find(session, item => item.Id == operation.ResourceId)
                    .FirstOrDefaultAsync(token);
                var value = behavior.WriteIncorrectValue ? 999 :
                    DeclarationJson.Read<WidgetSpec>(operation.Declaration.Spec).Value;
                var next = new TestWidget(operation.ResourceId, operation.Context.TenantId,
                    operation.Declaration.Address.Name, value, (current?.Revision ?? 0) + 1, current?.Fence ?? 0);
                await widgets.ReplaceOneAsync(session, item => item.Id == next.Id, next,
                    new ReplaceOptions { IsUpsert = true }, token);
                return new(operation.ResourceId, Changed: true);
            }, ct);
        }
        private static DeclarationProjection Projection(int value) =>
            new(DeclarationJson.Write(new { }), DeclarationJson.Write(new WidgetSpec(value)));
    }
}
