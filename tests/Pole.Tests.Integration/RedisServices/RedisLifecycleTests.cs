using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Postgres.Domain;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Domain;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Application.Lifecycle;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.RedisServices;

public sealed class RedisLifecycleTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private readonly ReviewedAuthority authorization = new();
    protected override void ConfigureServices(IServiceCollection services)
    {
        Pole.Tests.Integration.GitOps.NamedCatalogTestData.ConfigureIsolatedApi(services);
        services.AddSingleton<IGitOpsMutations>(authorization);
        services.AddSingleton<IGitLifecyclePlanGuard>(authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumer_deletion_preserves_git_owned_bindings_and_allows_unowned_bindings(bool gitOwned)
    {
        var resource = await Seed("RedisService");
        var current = await Services.GetRequiredService<IMongoCollection<Redis>>()
            .Find(item => item.Id == resource.ResourceId).SingleAsync();
        var group = await DbContext.ResourceGroups.Find(_ => true).SingleAsync();
        var secret = await ConsumerSecretDeletionTestData.InsertAsync(Fixture.Database, group,
            RedisResourceDeployment.ResourceType, current.Id);
        if (gitOwned)
            authorization.Managed.Add(("SecretBinding", secret.Bindings.Single().Id));
        var events = await DbContext.OutboxMessages.CountDocumentsAsync(_ => true);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var result = await Services.GetRequiredService<BeginRedisDeletion>()(
            new(current, current.BeginDeletion(DateTime.UtcNow)), session, default);
        if (gitOwned)
        {
            var required = Assert.IsType<RedisSecretBindingRemovalRequired>(result.Value);
            Assert.Equal(409, RedisHttpMappings.MapSecretBindingRemovalRequired(required).StatusCode);
        }
        else
            Assert.IsType<RedisDeletionStarted>(result.Value);
        await session.AbortTransactionAsync();
        Assert.Equal(events, await DbContext.OutboxMessages.CountDocumentsAsync(_ => true));
        Assert.Equal(current.ToBsonDocument(), (await Services.GetRequiredService<IMongoCollection<Redis>>()
            .Find(item => item.Id == current.Id).SingleAsync()).ToBsonDocument());
        if (gitOwned)
        {
            session.StartTransaction();
            var provider = Services.GetServices<IDeclarativeResourceLifecycleProvider>()
                .Single(item => item.Kind == "RedisService");
            var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>((await provider.ReadLifecycleAsync(
                new(current.TenantId, resource), session, default)).Value);
            Assert.IsType<GitLifecycleDependencyRetained>(Assert.IsType<DeclarationReadBlocked>((await
                provider.RemoveAsync(new(authorization.Authority, resource, snapshot.Version), session,
                    default)).Value).Failure);
            await session.AbortTransactionAsync();
        }
    }

    [Fact]
    public async Task Lifecycle_deletion_requires_an_explicit_reviewed_connection_set()
    {
        var resource = await Seed("RedisService");
        var current = await Services.GetRequiredService<IMongoCollection<Redis>>()
            .Find(item => item.Id == resource.ResourceId).SingleAsync();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var result = await Services.GetRequiredService<BeginRedisDeletion>()(
            new(current, current.BeginDeletion(DateTime.UtcNow), authorization.Authority), session, default);
        Assert.IsType<RedisDeletionConflict>(result.Value);
        await session.AbortTransactionAsync();
    }

    [Theory]
    [InlineData("RedisService")]
    public async Task Workload_removal_is_atomic_with_quota_and_deployment_and_waits_for_finalization(string kind)
    {
        var resource = await Seed(kind);
        var provider = Services.GetServices<IDeclarativeResourceLifecycleProvider>().Single(item => item.Kind == kind);
        var usages = Services.GetRequiredService<IMongoCollection<TenantQuotaUsage>>();
        var initial = QuotaDomainFactory.CreateUsage(TestData.TenantId, DateTime.UtcNow) with
        {
            Values = ImmutableList.Create(new QuotaValue(CpuQuota.QuotaKey, 250),
                new QuotaValue(RamQuota.QuotaKey, 256))
        };
        await usages.InsertOneAsync(initial);
        var baselineEvents = await DbContext.OutboxMessages.CountDocumentsAsync(_ => true);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>((await provider.ReadLifecycleAsync(
            new(TestData.TenantId, resource), session, default)).Value);
        Assert.Equal(GitLifecycleProtection.ProtectedData, snapshot.Protection);
        var request = new RemoveLifecycleResourceRequest(authorization.Authority, resource, snapshot.Version);
        Assert.IsType<GitLifecycleRemovalRequested>((await provider.RemoveAsync(request, session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(baselineEvents, await DbContext.OutboxMessages.CountDocumentsAsync(_ => true));
        Assert.Equal(initial.Values,
            (await usages.Find(item => item.TenantId == TestData.TenantId).SingleAsync()).Values);
        session.StartTransaction();
        Assert.Equal(GitLifecyclePresence.Active, Assert.IsType<GitLifecycleResourceSnapshot>(
            (await provider.ReadLifecycleAsync(new(TestData.TenantId, resource), session, default)).Value).Presence);
        Assert.IsType<GitLifecycleRemovalRequested>((await provider.RemoveAsync(request, session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.True(await DbContext.OutboxMessages.CountDocumentsAsync(_ => true) > baselineEvents);
        var released = await usages.Find(item => item.TenantId == TestData.TenantId).SingleAsync();
        Assert.Equal(0, QuotaValues.GetValue(released.Values, CpuQuota.QuotaKey));
        var eventCount = await DbContext.OutboxMessages.CountDocumentsAsync(_ => true);
        session.StartTransaction();
        var pending = Assert.IsType<GitLifecycleResourceSnapshot>((await provider.ReadLifecycleAsync(
            new(TestData.TenantId, resource), session, default)).Value);
        Assert.Equal(GitLifecyclePresence.Deleting, pending.Presence);
        Assert.IsType<GitLifecycleRemovalRequested>((await provider.RemoveAsync(request with
        { Expected = pending.Version }, session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.Equal(eventCount, await DbContext.OutboxMessages.CountDocumentsAsync(_ => true));
        Assert.Equal(released.AdmissionRevision,
            (await usages.Find(item => item.TenantId == TestData.TenantId).SingleAsync()).AdmissionRevision);
    }

    [Theory]
    [InlineData("RedisService")]
    public async Task Changed_root_precondition_and_other_tenant_cannot_delete_workload(string kind)
    {
        var resource = await Seed(kind);
        var provider = Services.GetServices<IDeclarativeResourceLifecycleProvider>().Single(item => item.Kind == kind);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var current = Assert.IsType<GitLifecycleResourceSnapshot>((await provider.ReadLifecycleAsync(
            new(TestData.TenantId, resource), session, default)).Value);
        Assert.IsType<GitLifecycleResourceAbsent>((await provider.ReadLifecycleAsync(new(Id(), resource), session,
            default)).Value);
        var changed = new DeclarationVersionToken(current.Version.Documents.Select(item =>
            item.DocumentId.StartsWith(kind + "/", StringComparison.Ordinal)
                ? item with { Revision = item.Revision + 1 } : item).ToArray());
        Assert.IsType<DeclarationPreconditionsChanged>((await provider.RemoveAsync(
            new(authorization.Authority, resource, changed), session, default)).Value);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task Deletion_intent_survives_runtime_claim_and_completion_but_rejects_managed_changes()
    {
        var resource = await Seed("RedisService");
        var provider = Services.GetServices<IDeclarativeResourceLifecycleProvider>()
            .Single(item => item.Kind == resource.Address.Kind);
        await Services.GetRequiredService<IMongoCollection<TenantQuotaUsage>>().InsertOneAsync(
            QuotaDomainFactory.CreateUsage(TestData.TenantId, DateTime.UtcNow) with
            {
                Values = ImmutableList.Create(new QuotaValue(CpuQuota.QuotaKey, 250),
                    new QuotaValue(RamQuota.QuotaKey, 256))
            });
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var active = Assert.IsType<GitLifecycleResourceSnapshot>((await provider.ReadLifecycleAsync(
            new(TestData.TenantId, resource), session, default)).Value);
        Assert.IsType<GitLifecycleRemovalRequested>((await provider.RemoveAsync(
            new(authorization.Authority, resource, active.Version), session, default)).Value);
        await session.CommitTransactionAsync();

        async Task<DeclarationVersionToken> Read()
        {
            session.StartTransaction();
            var current = Assert.IsType<GitLifecycleResourceSnapshot>((await provider.ReadLifecycleAsync(
                new(TestData.TenantId, resource), session, default)).Value);
            await session.CommitTransactionAsync();
            Assert.Equal(GitLifecyclePresence.Deleting, current.Presence);
            return current.Version;
        }

        var baseline = await Read();
        var deployment = await DbContext.ResourceDeployments.Find(item => item.ResourceId == resource.ResourceId)
            .SingleAsync();
        var start = deployment.UpdatedAtUtc.AddSeconds(1);
        var claim = Id();
        var reconciling = deployment.Start(deployment.DesiredGeneration, ResourceDeploymentOperation.Delete,
            claim, start, start.AddMinutes(2));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id, reconciling);
        Assert.Equal(baseline.Documents, (await Read()).Documents);
        var completed = reconciling.Complete(deployment.DesiredGeneration, ResourceDeploymentOperation.Delete,
            claim, [], start.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id, completed);
        Assert.Equal(baseline.Documents, (await Read()).Documents);

        var selectors = completed with
        {
            ResolvedPlacement = completed.ResolvedPlacement with
            {
                Compute = completed.ResolvedPlacement.Compute with
                {
                    NodeSelector = ImmutableList.Create(ResolvedComputeNodeSelector.Create("zone", "one"),
                    ResolvedComputeNodeSelector.Create("pool", "two"))
                }
            }
        };
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id, selectors);
        var ordered = await Read();
        var reversed = selectors with
        {
            ResolvedPlacement = selectors.ResolvedPlacement with
            {
                Compute = selectors.ResolvedPlacement.Compute with
                { NodeSelector = selectors.ResolvedPlacement.Compute.NodeSelector.Reverse().ToImmutableList() }
            }
        };
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id, reversed);
        Assert.Equal(ordered.Documents, (await Read()).Documents);

        var changes = new[]
        {
            completed with { DesiredGeneration = completed.DesiredGeneration + 1 },
            completed with { ResourceRevision = completed.ResourceRevision + 1 },
            completed with { FixedReplicas = completed.FixedReplicas + 1 },
            completed with { ResolvedPlacement = completed.ResolvedPlacement with
                { Storage = completed.ResolvedPlacement.Storage! with { StorageClassName = "changed" } } },
            completed with { ResolvedPlacement = completed.ResolvedPlacement with
                { Compute = completed.ResolvedPlacement.Compute with { ComputeProfileRevision = 9 } } }
        };
        foreach (var changed in changes)
        {
            await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id, changed);
            Assert.False(baseline.Documents.SequenceEqual((await Read()).Documents));
        }
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id, completed);
        await Services.GetRequiredService<IMongoCollection<Redis>>().UpdateOneAsync(
            item => item.Id == resource.ResourceId,
            Builders<Redis>.Update.Set(item => item.Tags, ImmutableHashSet.Create("changed")));
        Assert.False(baseline.Documents.SequenceEqual((await Read()).Documents));
    }

    [Theory]
    [InlineData(GitLifecycleAction.Remove)]
    [InlineData(GitLifecycleAction.Replace)]
    public async Task Lifecycle_warnings_describe_native_PVC_delete_policy_without_promising_volume_retention(
        GitLifecycleAction action)
    {
        var resource = await Seed("RedisService");
        var collection = Services.GetRequiredService<IMongoCollection<Redis>>();
        var before = await collection.Find(item => item.Id == resource.ResourceId).SingleAsync();
        var provider = Assert.IsAssignableFrom<IDeclarativeLifecycleWarningProvider>(Services
            .GetServices<IDeclarativeResourceProvider>().Single(item => item.Kind == "RedisService"));
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var request = new ReadLifecycleWarningsRequest(before.TenantId, before.Id, action);
        var codes = await provider.ReadLifecycleWarningsAsync(request, session, default);
        Assert.Equal(new[]
        {
            "PersistentVolumeReclaimPolicyDeterminesDataRetention", "RedisPersistentVolumeClaimsDeleted"
        }, codes.Order(StringComparer.Ordinal));
        Assert.Empty(await provider.ReadLifecycleWarningsAsync(request with { TenantId = Id() }, session, default));
        Assert.Empty(await provider.ReadLifecycleWarningsAsync(request with { ResourceId = Id() }, session, default));
        await session.CommitTransactionAsync();
        Assert.Equal(before.ToBsonDocument(), (await collection.Find(item => item.Id == before.Id).SingleAsync())
            .ToBsonDocument());
    }

    private async Task<GitLifecycleResourceReference> Seed(string kind)
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var target = TestData.SeedDeploymentTarget().AttestForTests() with { TechnicalName = "lab" };
        await Fixture.Database.GetCollection<DeploymentTarget>("DeploymentTargets").InsertOneAsync(target);
        var id = Id();
        var type = ResourceTypeId.Create(kind == "RedisService" ? "pole/redis" : "pole/postgresql");
        var computes = Services.GetRequiredService<IMongoCollection<ComputeProfile>>();
        var profile = await computes.Find(item => item.Id == TestData.DefaultComputeProfileId).SingleAsync();
        await computes.ReplaceOneAsync(item => item.Id == profile.Id,
            profile with { AllowedResourceTypes = profile.AllowedResourceTypes.Add(type.Value) });
        var stores = Services.GetRequiredService<IMongoCollection<StorageProfile>>();
        var storage = await stores.Find(item => item.Id == TestData.DefaultStorageProfileId).SingleAsync();
        await stores.ReplaceOneAsync(item => item.Id == storage.Id,
            storage with { AllowedResourceTypes = storage.AllowedResourceTypes.Add(type.Value) });
        await Services.GetRequiredService<IMongoCollection<Redis>>().InsertOneAsync(RedisFactory.Create(group.TenantId,
            "lifecycle-workload", group.Id, new RedisSpec(new Storage(10, TestData.DefaultStorageProfileId)),
            RedisFactory.DefaultCreateOptions with { Id = id }));
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<RegisteredResource>((await Services.GetRequiredService<IResourceRegistrar>().RegisterAsync(
            new(group.TenantId, group.Id, new(type, id, "lifecycle-workload")), session, default)).Value);
        var compute = new ResourceDeploymentComputeConfiguration(TestData.DefaultComputeProfileId, 1,
            TestData.DefaultDeploymentTargetId, "default", new(250, 256), new(1000, 1024), ComputeArchitecture.Any,
            [], [], [], [], [], null);
        Assert.IsType<PlacementSummaryDto>((await Services.GetRequiredService<CreateResourceDeployment>()(
            new(Id(), group.TenantId, type, id, group.Id, group.DefaultIsolationScopeId,
                new(TestData.DefaultDeploymentTargetId, target.WorkloadConfiguration.Revision, null), compute,
                new(TestData.DefaultStorageProfileId, 1, "standard"), ResourcePlacementCapability.SingleTarget),
            session, default)).Value);
        await session.CommitTransactionAsync();
        return new(new(kind, "lifecycle-workload"), id);
    }

    private static string Id() => ObjectId.GenerateNewId().ToString();

    // Isolate the owning feature's transaction boundary; the GitOps suite exercises real admission.
    private sealed class ReviewedAuthority : IGitOpsMutations, IGitLifecyclePlanGuard
    {
        public Dictionary<ResourceAddress, string> References { get; } = [];
        public HashSet<(string Kind, string Id)> Managed { get; } = [];
        public GitLifecycleAuthority Authority { get; } = new(TestData.TenantId, "source", "run", 1, 1, 1,
            "worker", 1, "preview", "reviewed");
        public GitLifecycleAuthority? Observed { get; private set; }
        public Task<ReadLifecycleApprovedScopeOutcome> ReadScopeAsync(GitLifecycleAuthority authority,
            IClientSessionHandle session, CancellationToken ct) => Task.FromResult<ReadLifecycleApprovedScopeOutcome>(
                authority == Authority ? new GitLifecycleApprovedScope([]) : new StaleGitExecution());

        public Task<bool> AuthorizeAsync(AuthorizeGitResourceMutationRequest request,
            IClientSessionHandle session, CancellationToken ct)
        {
            Observed = request.Lifecycle;
            return Task.FromResult(request.Lifecycle is null ? !Managed.Contains((request.Kind, request.ResourceId)) :
                request.Lifecycle == Authority && request.LifecycleEffect == GitLifecycleMutationEffect.Remove);
        }

        public Task<GitOperationResult> ExecuteAsync(GitOperation operation,
            Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<GitOperationResult?> BeginAsync(GitOperation operation, IClientSessionHandle session,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<GitOperationResult> CompleteAsync(CompleteGitOperationRequest request,
            IClientSessionHandle session, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> ResolveAsync(ResolveGitResourceRequest request, IClientSessionHandle session,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> ResolveAcceptedAsync(ResolveAcceptedGitReferenceRequest request,
            IClientSessionHandle session, CancellationToken ct) => Task.FromResult(
                References.GetValueOrDefault(request.Address));
        public Task<PinGitReferenceOutcome> PinReferenceAsync(PinGitReferenceRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, GitResourceStatus>> StatusesAsync(
            LoadGitResourceStatusesRequest request,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<GitResourceStatus?> StatusAsync(LoadGitResourceStatusRequest request,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
