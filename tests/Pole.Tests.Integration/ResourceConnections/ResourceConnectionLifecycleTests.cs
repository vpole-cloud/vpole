// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceConnections;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class ResourceConnectionLifecycleTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private const string DeletionEvent = "pole.resource-connections/v1/resource-connection-deletion-requested";
    private ReviewedAuthority? _authorization;
    private ReviewedAuthority authorization => _authorization ??= new ReviewedAuthority(Data.TenantId);

    private IMongoCollection<ResourceConnection> Connections =>
        Fixture.Database.GetCollection<ResourceConnection>("ResourceConnections");

    private IDeclarativeResourceLifecycleProvider Provider =>
        Services.GetServices<IDeclarativeResourceLifecycleProvider>().Single(item => item.Kind == "ResourceConnection");

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IGitOpsMutations>(authorization);

    [Fact]
    public async Task Reads_use_exact_tenant_and_id_and_report_pending_cleanup()
    {
        var connection = Connection();
        await Connections.InsertOneAsync(connection);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var request = Read(connection);
        var current = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider.ReadLifecycleAsync(request, session, default)).Value);
        Assert.Equal(GitLifecyclePresence.Active, current.Presence);
        Assert.Equal(GitLifecycleProtection.ConfigurationOnly, current.Protection);
        Assert.Equal(
            new ResourceConnectionPlanningVersion(connection.Id, connection.Revision, connection.ReferenceFence),
            ResourceConnectionPlanningVersion.FromDocuments(
                connection.Id,
                current.Version.Documents.ToDictionary(item => item.DocumentId, item => item.Revision)));
        Assert.IsType<GitLifecycleResourceAbsent>(
            (await Provider.ReadLifecycleAsync(
                request with
                {
                    TenantId = ObjectId.GenerateNewId().ToString(),
                },
                session,
                default)).Value);
        await session.AbortTransactionAsync();

        await Connections.ReplaceOneAsync(item => item.Id == connection.Id, connection.BeginDeletion(DateTime.UtcNow));
        session.StartTransaction();
        Assert.Equal(
            GitLifecyclePresence.Deleting,
            Assert.IsType<GitLifecycleResourceSnapshot>(
                    (await Provider.ReadLifecycleAsync(request, session, default)).Value)
                .Presence);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task Joined_removal_and_outbox_rollback_with_the_callers_transaction()
    {
        var connection = Connection();
        await Connections.InsertOneAsync(connection);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider.ReadLifecycleAsync(Read(connection), session, default)).Value);
        var request = new RemoveLifecycleResourceRequest(authorization.Authority, snapshot.Resource, snapshot.Version);
        Assert.IsType<GitLifecycleRemovalRequested>((await Provider.RemoveAsync(request, session, default)).Value);
        Assert.Equal(authorization.Authority, authorization.Observed);
        Assert.Equal(GitLifecycleMutationEffect.Remove, authorization.Effect);
        await session.AbortTransactionAsync();

        Assert.IsType<ActiveResourceConnection>((await Load(connection.Id)).Lifecycle);
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(Data.TenantId, DeletionEvent));
        session.StartTransaction();
        Assert.IsType<GitLifecycleRemovalRequested>((await Provider.RemoveAsync(request, session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.IsType<DeletingResourceConnection>((await Load(connection.Id)).Lifecycle);
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(Data.TenantId, DeletionEvent));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("reference-fence")]
    [InlineData("authority")]
    public async Task Changed_preconditions_and_authority_do_not_start_deletion(string change)
    {
        var connection = Connection();
        await Connections.InsertOneAsync(connection);
        using var session = await Fixture.Client.StartSessionAsync();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider.ReadLifecycleAsync(Read(connection), session, default)).Value);
        if (change != "authority")
            await Connections.UpdateOneAsync(
                item => item.Id == connection.Id,
                change == "revision"
                    ? Builders<ResourceConnection>.Update.Inc(item => item.Revision, 1)
                    : Builders<ResourceConnection>.Update.Inc(item => item.ReferenceFence, 1));

        var authority = change == "authority"
            ? authorization.Authority with { ActionId = "unreviewed" }
            : authorization.Authority;
        session.StartTransaction();
        var outcome = await Provider.RemoveAsync(
            new RemoveLifecycleResourceRequest(authority, snapshot.Resource, snapshot.Version),
            session,
            default);
        if (change == "authority")
            Assert.IsType<StaleGitExecution>(outcome.Value);
        else
            Assert.IsType<DeclarationPreconditionsChanged>(outcome.Value);
        await session.AbortTransactionAsync();
        Assert.IsType<ActiveResourceConnection>((await Load(connection.Id)).Lifecycle);
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(Data.TenantId, DeletionEvent));
    }

    [Fact]
    public async Task Replay_does_not_repeat_the_event_and_completion_waits_for_purge()
    {
        var connection = Connection();
        await Connections.InsertOneAsync(connection);
        using var session = await Fixture.Client.StartSessionAsync();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            session.StartTransaction();
            var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
                (await Provider.ReadLifecycleAsync(Read(connection), session, default)).Value);
            Assert.IsType<GitLifecycleRemovalRequested>(
                (await Provider.RemoveAsync(
                    new RemoveLifecycleResourceRequest(authorization.Authority, snapshot.Resource, snapshot.Version),
                    session,
                    default)).Value);
            await session.CommitTransactionAsync();
        }

        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(Data.TenantId, DeletionEvent));
        var deleting = await Load(connection.Id);
        var location = Assert.Single(Assert.IsType<DeletingResourceConnection>(deleting.Lifecycle).Targets)
            .Snapshot.PolicyLocation;
        var completed = Assert.IsType<ConnectionCleanupRecorded>(
                deleting.RecordCleanupCompleted(location, deleting.Generation, DateTime.UtcNow).Value)
            .Connection;
        await Connections.ReplaceOneAsync(item => item.Id == completed.Id, completed);
        await Services.GetRequiredService<PurgeResourceConnectionIfComplete>()(completed);
        Assert.NotNull(
            await Services.GetRequiredService<LoadResourceConnectionTombstone>()(completed.TenantId, completed.Id));

        // A new same-name connection must not be mistaken for the removed accepted identity.
        await Connections.InsertOneAsync(Connection());
        Assert.IsType<GitLifecycleResourceAbsent>(
            (await Provider.ReadLifecycleAsync(Read(connection), session, default)).Value);
    }

    [Fact]
    public async Task New_reverse_reference_invalidates_an_empty_reviewed_set()
    {
        var connection = Connection();
        var dependencyProvider = Assert.IsAssignableFrom<IDeclarativeLifecycleDependencyProvider>(Provider);
        var resource = new GitLifecycleResourceReference(
            new ResourceAddress("WebService", "caller"),
            connection.SourceResourceId);
        using var session = await Fixture.Client.StartSessionAsync();
        var request = new ReadLifecycleDependenciesRequest(connection.TenantId, resource);
        Assert.Empty(await dependencyProvider.ReadDependenciesAsync(request, session, default));
        await Connections.InsertOneAsync(connection);
        session.StartTransaction();
        Assert.IsType<DeclarationPreconditionsChanged>(
            (await dependencyProvider.FenceDependenciesAsync(
                new FenceLifecycleDependenciesRequest(connection.TenantId, resource, []),
                session,
                default)).Value);
        await session.AbortTransactionAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("revision")]
    [InlineData("reference-fence")]
    [InlineData("deleting-unreviewed")]
    public async Task Cascade_rejects_any_difference_from_the_complete_reviewed_set(string change)
    {
        var connection = Connection();
        await Connections.InsertOneAsync(connection);
        var reviewed = new ResourceConnectionPlanningVersion(
            connection.Id,
            connection.Revision,
            connection.ReferenceFence);
        IReadOnlyList<ResourceConnectionPlanningVersion> expected = change switch
        {
            "missing" => [],
            "extra" => [reviewed, reviewed with { ConnectionId = ObjectId.GenerateNewId().ToString() }],
            "duplicate" => [reviewed, reviewed],
            "revision" => [reviewed with { Revision = reviewed.Revision + 1 }],
            "reference-fence" => [reviewed with { ReferenceFence = reviewed.ReferenceFence + 1 }],
            "deleting-unreviewed" => [reviewed],
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        if (change == "deleting-unreviewed")
        {
            var unreviewed = Connection() with { Name = "unreviewed", SourceResourceId = connection.SourceResourceId };
            await Connections.InsertOneAsync(unreviewed.BeginDeletion(DateTime.UtcNow));
        }

        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var result = await Services.GetRequiredService<CascadeConnectionsForResourceDeletion>()(
            new CascadeConnectionsForResourceDeletionRequest(
                connection.TenantId,
                ResourceTypeId.Create(connection.SourceResourceTypeId),
                connection.SourceResourceId,
                expected),
            session);
        Assert.IsType<ResourceConnectionCascadeConflict>(result.Value);
        await session.AbortTransactionAsync();
        Assert.IsType<ActiveResourceConnection>((await Load(connection.Id)).Lifecycle);
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(Data.TenantId, DeletionEvent));
    }

    [Fact]
    public async Task Reviewed_cascade_is_atomic_and_preserves_already_deleting_entries()
    {
        var connection = Connection();
        var deleting = Connection() with { Name = "already-deleting", SourceResourceId = connection.SourceResourceId };
        deleting = deleting.BeginDeletion(DateTime.UtcNow);
        await Connections.InsertManyAsync([connection, deleting]);
        var reviewed = new[] { connection, deleting }.Select(item =>
                new ResourceConnectionPlanningVersion(item.Id, item.Revision, item.ReferenceFence))
            .ToArray();
        var request = new CascadeConnectionsForResourceDeletionRequest(
            connection.TenantId,
            ResourceTypeId.Create(connection.SourceResourceTypeId),
            connection.SourceResourceId,
            reviewed);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var observed = await Services.GetRequiredService<LoadResourceConnectionCascadePlanning>()(
            new LoadResourceConnectionCascadePlanningRequest(
                connection.TenantId,
                request.ResourceType,
                request.ResourceId),
            session,
            default);
        Assert.Equal(reviewed.OrderBy(item => item.ConnectionId, StringComparer.Ordinal), observed);
        Assert.Equal(
            1,
            Assert.IsType<ResourceConnectionsCascaded>(
                    (await Services.GetRequiredService<CascadeConnectionsForResourceDeletion>()(request, session))
                    .Value)
                .Count);
        await session.AbortTransactionAsync();
        Assert.IsType<ActiveResourceConnection>((await Load(connection.Id)).Lifecycle);
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(Data.TenantId, DeletionEvent));
        session.StartTransaction();
        Assert.Equal(
            1,
            Assert.IsType<ResourceConnectionsCascaded>(
                    (await Services.GetRequiredService<CascadeConnectionsForResourceDeletion>()(request, session))
                    .Value)
                .Count);
        await session.CommitTransactionAsync();
        Assert.IsType<DeletingResourceConnection>((await Load(connection.Id)).Lifecycle);
        var retained = await Load(deleting.Id);
        Assert.Equal(deleting.Revision, retained.Revision);
        Assert.Equal(deleting.ReferenceFence + 1, retained.ReferenceFence);
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(Data.TenantId, DeletionEvent));
    }

    [Fact]
    public async Task Registration_fence_rejects_a_connection_admitted_after_the_transaction_snapshot()
    {
        var connection = Connection();
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var resourceType = ResourceTypeId.Create(connection.SourceResourceTypeId);
        using var seed = await Fixture.Client.StartSessionAsync();
        seed.StartTransaction();
        Assert.IsType<RegisteredResource>(
            (await Services.GetRequiredService<IResourceRegistrar>()
                .RegisterAsync(
                    new RegisterResourceRequest(
                        connection.TenantId,
                        group.Id,
                        new ResourceRegistrationDescriptor(resourceType, connection.SourceResourceId, "caller")),
                    seed)).Value);
        await seed.CommitTransactionAsync();
        var resource = new GitLifecycleResourceReference(
            new ResourceAddress("WebService", "caller"),
            connection.SourceResourceId);
        var dependencies = Assert.IsAssignableFrom<IDeclarativeLifecycleDependencyProvider>(Provider);
        using var stale = await Fixture.Client.StartSessionAsync();
        stale.StartTransaction();
        Assert.Empty(
            await dependencies.ReadDependenciesAsync(
                new ReadLifecycleDependenciesRequest(connection.TenantId, resource),
                stale,
                default));
        using var admission = await Fixture.Client.StartSessionAsync();
        admission.StartTransaction();
        Assert.NotNull(
            await Services.GetRequiredService<FenceRegisteredResource>()(
                new FenceRegisteredResourceRequest(connection.TenantId, resourceType, connection.SourceResourceId),
                admission));
        await Connections.InsertOneAsync(admission, connection);
        await admission.CommitTransactionAsync();

        await Assert.ThrowsAsync<MongoCommandException>(() => dependencies.FenceDependenciesAsync(
            new FenceLifecycleDependenciesRequest(connection.TenantId, resource, []),
            stale,
            default));
        await stale.AbortTransactionAsync();
        Assert.IsType<ActiveResourceConnection>((await Load(connection.Id)).Lifecycle);
    }

    private Task<ResourceConnection> Load(string id) => Connections.Find(item => item.Id == id).SingleAsync();

    private static ReadLifecycleResourceRequest Read(ResourceConnection connection) => new(
        connection.TenantId,
        new GitLifecycleResourceReference(new ResourceAddress("ResourceConnection", connection.Name), connection.Id));

    private ResourceConnection Connection() => ResourceConnectionFactory.Create(
        ObjectId.GenerateNewId().ToString(),
        Data.TenantId,
        "reviewed-connection",
        ResourceTypeId.Create("pole/web-service"),
        ObjectId.GenerateNewId().ToString(),
        ResourceTypeId.Create("pole/redis"),
        ObjectId.GenerateNewId().ToString(),
        "redis",
        [
            ConnectionTargetSnapshot.Create(
                "target",
                "destination-deployment",
                "cell",
                "namespace",
                "service",
                6379,
                6379,
                [new ConnectionSourcePlacement("source-deployment", "cell", "namespace")]),
        ]);

    // Isolate the owning feature's transaction boundary; the GitOps suite exercises real admission.
    private sealed class ReviewedAuthority(
        string tenantId
    ) : IGitOpsMutations
    {
        public GitLifecycleAuthority Authority { get; } = new(
            tenantId,
            "source",
            "run",
            1,
            1,
            1,
            "worker",
            1,
            "preview",
            "reviewed");

        public GitLifecycleAuthority? Observed { get; private set; }
        public GitLifecycleMutationEffect? Effect { get; private set; }

        public Task<bool> AuthorizeAsync(
            AuthorizeGitResourceMutationRequest request,
            IClientSessionHandle session,
            CancellationToken ct)
        {
            Observed = request.Lifecycle;
            Effect = request.LifecycleEffect;
            return Task.FromResult(
                request.Lifecycle == Authority && request.LifecycleEffect == GitLifecycleMutationEffect.Remove);
        }

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
