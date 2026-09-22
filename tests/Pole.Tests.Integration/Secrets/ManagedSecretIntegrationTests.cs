using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Pole.Platform.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.Migrations;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

public sealed class ManagedSecretIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private static string Id() => ObjectId.GenerateNewId().ToString();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new SecretIndexesMigration().Up(Fixture.Database);
        // Exercise the same assembly-scanning composition used by Pole.Server (empty extra migrations).
        var services = new ServiceCollection().AddPolePlatform(new ConfigurationBuilder().Build(), [], [], []);
        using var production = services.BuildServiceProvider();
        var projectionMigration = Assert.Single(production.GetServices<IMigration>().OfType<SecretProjectionIndexesMigration>());
        await projectionMigration.Up(Fixture.Database);
    }

    [Fact]
    public async Task Staged_owner_placement_and_bound_secret_commit_atomically_and_protect_provider_mutations()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepared = Assert.IsType<Ok<PreparedManagedSecret>>((await Services.GetRequiredService<PrepareManagedSecret>()(
            group.TenantId, Id(), owner, "database-credentials", 1, new Dictionary<string, string> { ["password"] = "private-value" }, ManagedSecretKubernetesType.Opaque, default)).Value).Value;
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<Ok<Unit>>((await Services.GetRequiredService<CreateBoundManagedSecret>()(
            prepared, group.Id, group.DefaultIsolationScopeId, deployment.Id, session, default)).Value);
        await session.CommitTransactionAsync();
        var secret = Assert.IsType<Secret>(await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, owner, default));
        Assert.Single(secret.Bindings);
        Assert.IsType<SecretManagedByProvider>((await Services.GetRequiredService<RemoveSecretBinding>()(
            group.TenantId, secret.Id, secret.Bindings[0].Id, default)).Value);
        Assert.IsType<SecretManagedByProvider>((await Services.GetRequiredService<PersistRotatedSecret>()(
            group.TenantId, secret.Id, 1, new Dictionary<string, string> { ["password"] = "bypass" }, default)).Value);
        var values = Assert.IsType<Ok<ManagedSecretValues>>((await Services.GetRequiredService<ReadManagedSecret>()(group.TenantId, owner, default)).Value).Value;
        Assert.Equal("private-value", values.Values["password"]);
        var canonicalRead = await Services.GetRequiredService<ReadManagedSecret>()(group.TenantId.ToUpperInvariant(),
            owner with { ResourceId = owner.ResourceId.ToUpperInvariant() }, default);
        Assert.Equal("private-value", Assert.IsType<Ok<ManagedSecretValues>>(canonicalRead.Value).Value.Values["password"]);
        Assert.DoesNotContain("private-value", values.ToString());
        Assert.IsType<Err<ManagedSecretError>>((await Services.GetRequiredService<ReadManagedSecret>()(Id(), owner, default)).Value);
        await Services.GetRequiredService<RemoveConsumerSecretBindings>()(group.TenantId, group.Id, owner.ResourceType, owner.ResourceId, default);
        var retained = Assert.IsType<Secret>(await Services.GetRequiredService<LoadSecretById>()(group.TenantId, secret.Id, default));
        Assert.IsType<UnmanagedSecret>(retained.Management);
        Assert.Empty(retained.Bindings);
        Assert.IsType<Secret>((await Services.GetRequiredService<PersistRotatedSecret>()(group.TenantId, retained.Id, 1,
            new Dictionary<string, string> { ["password"] = "fallback-rotated" }, default)).Value);
    }

    [Fact]
    public async Task Aborting_owner_creation_removes_encrypted_secret_binding_registration_and_outbox()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepared = Assert.IsType<Ok<PreparedManagedSecret>>((await Services.GetRequiredService<PrepareManagedSecret>()(
            group.TenantId, Id(), owner, "abort-credentials", 1, new Dictionary<string, string> { ["password"] = "private-value" }, ManagedSecretKubernetesType.Opaque, default)).Value).Value;
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<Ok<Unit>>((await Services.GetRequiredService<CreateBoundManagedSecret>()(
            prepared, group.Id, group.DefaultIsolationScopeId, deployment.Id, session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Null(await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, owner, default));
        Assert.Equal(0, await Fixture.Database.GetCollection<SecretVersion>("SecretVersions").CountDocumentsAsync(x => x.SecretId == prepared.SecretId));
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync("pole.secrets/v1/secret-changed"));
        Assert.Equal(0, await DbContext.ResourceDeployments.CountDocumentsAsync(x => x.Id == deployment.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_release_deletes_unshared_credentials_but_retains_shared_secret_as_unmanaged(bool shared)
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepared = Assert.IsType<Ok<PreparedManagedSecret>>((await Services.GetRequiredService<PrepareManagedSecret>()(
            group.TenantId, Id(), owner, "shared-credentials", 1, new Dictionary<string, string> { ["password"] = "private-value" }, ManagedSecretKubernetesType.Opaque, default)).Value).Value;
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<Ok<Unit>>((await Services.GetRequiredService<CreateBoundManagedSecret>()(
            prepared, group.Id, group.DefaultIsolationScopeId, deployment.Id, session, default)).Value);
        var consumer = new ManagedSecretOwner("pole/web-service", Id());
        await StageConsumer(group, consumer, session);
        await session.CommitTransactionAsync();
        if (shared)
            Assert.IsType<Secret>((await Services.GetRequiredService<PersistSecretBinding>()(group.TenantId, prepared.SecretId,
                SecretBinding.Create(Id(), consumer.ResourceType, consumer.ResourceId, new AllSecretDeployments(), []), default)).Value);
        session.StartTransaction();
        Assert.IsType<Ok<Unit>>((await Services.GetRequiredService<ReleaseManagedSecret>()(group.TenantId, owner, session, default)).Value);
        await session.CommitTransactionAsync();
        var secret = await Services.GetRequiredService<LoadSecretById>()(group.TenantId, prepared.SecretId, default);
        if (shared)
        {
            Assert.NotNull(secret);
            Assert.IsType<UnmanagedSecret>(secret.Management);
            Assert.Equal(consumer.ResourceId, Assert.Single(secret.Bindings).ConsumerId);
            Assert.IsType<Secret>((await Services.GetRequiredService<PersistRotatedSecret>()(group.TenantId, secret.Id, 1,
                new Dictionary<string, string> { ["password"] = "now-ordinary" }, default)).Value);
        }
        else
        {
            Assert.Null(secret);
            Assert.Equal(0, await Fixture.Database.GetCollection<SecretVersion>("SecretVersions").CountDocumentsAsync(x => x.SecretId == prepared.SecretId));
        }
    }

    [Fact]
    public async Task Owner_cannot_resolve_another_consumers_projection_from_shared_secret()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepared = Assert.IsType<Ok<PreparedManagedSecret>>((await Services.GetRequiredService<PrepareManagedSecret>()(
            group.TenantId, Id(), owner, "owner-credentials", 1, new Dictionary<string, string> { ["password"] = "value" }, ManagedSecretKubernetesType.Opaque, default)).Value).Value;
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<Ok<Unit>>((await Services.GetRequiredService<CreateBoundManagedSecret>()(
            prepared, group.Id, group.DefaultIsolationScopeId, deployment.Id, session, default)).Value);
        await session.CommitTransactionAsync();
        var secret = Assert.IsType<Secret>(await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, owner, default));
        var otherBinding = SecretBinding.Create(Id(), "pole/web-service", Id(), new AllSecretDeployments(), []);
        var other = SecretProjectionFactory.Create(secret, otherBinding, Id(), "cell", "target", "namespace").Applied();
        await Fixture.Database.GetCollection<SecretProjection>("SecretProjections").InsertOneAsync(other);
        var collection = new ServiceCollection().AddSecretsFeature();
        collection.AddSingleton<IMongoDatabase>(Fixture.Database);
        collection.AddSingleton(TimeProvider.System);
        collection.AddSingleton(Services.GetRequiredService<IEnvelopeEncryptionService>());
        collection.AddSingleton<LoadResourceDeploymentSet>((_, _, _, _) => Task.FromResult<IReadOnlyList<PlacementSummaryDto>>([]));
        collection.AddSingleton<LoadExactResourceDeploymentRuntimePlacement>((_, _, _, _, _) => Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(null));
        collection.AddSingleton<ClaimSecretProjection>((_, _, _, _) => Task.FromResult<SecretProjection?>(null));
        collection.AddSingleton<ISecretProjectionTarget>(new UnusedTarget());
        using var isolated = collection.BuildServiceProvider();
        var result = await isolated.GetRequiredService<EnsureManagedSecretProjection>()(group.TenantId, owner, other.DeploymentId, default);
        Assert.IsType<Err<ManagedSecretUnavailable>>(result.Value);
    }

    [Fact]
    public async Task Owner_recreation_cannot_reuse_previous_secret_projection_identity()
    {
        var binding = SecretBinding.Create(Id(), "pole/postgresql", Id(), new AllSecretDeployments(), []);
        var original = SecretFactory.CreateManaged(Id(), Id(), Id(), Id(), "owner-credentials", "credentials",
            ["password"], binding, KubernetesSecretType.Opaque, DateTime.UtcNow);
        // Preserve all other metadata to prove identity itself is fenced, independently of binding/version checks.
        var recreated = original with { Id = Id() };
        var projection = SecretProjectionFactory.Create(original, binding, Id(), "cell", "target", "namespace").Applied();
        var loads = 0;
        var collection = new ServiceCollection().AddSecretsFeature();
        collection.AddSingleton<IMongoDatabase>(Fixture.Database);
        collection.AddSingleton(TimeProvider.System);
        collection.AddSingleton(Services.GetRequiredService<IEnvelopeEncryptionService>());
        collection.AddSingleton<LoadManagedSecret>((_, _, _) => Task.FromResult<Secret?>(++loads == 1 ? original : recreated));
        collection.AddSingleton<LoadSecretById>((_, _, _) => Task.FromResult<Secret?>(null));
        collection.AddSingleton<LoadSecretProjections>((_, _, _) => Task.FromResult<IReadOnlyList<SecretProjection>>([projection]));
        collection.AddSingleton<LoadResourceDeploymentSet>((_, _, _, _) => Task.FromResult<IReadOnlyList<PlacementSummaryDto>>([]));
        collection.AddSingleton<LoadExactResourceDeploymentRuntimePlacement>((_, _, _, _, _) => Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(null));
        collection.AddSingleton<ClaimSecretProjection>((_, _, _, _) => Task.FromResult<SecretProjection?>(null));
        collection.AddSingleton<ISecretProjectionTarget>(new UnusedTarget());
        using var isolated = collection.BuildServiceProvider();
        var result = await isolated.GetRequiredService<EnsureManagedSecretProjection>()(original.TenantId,
            new(binding.ConsumerType, binding.ConsumerId), projection.DeploymentId, default);
        Assert.IsType<Err<ManagedSecretUnavailable>>(result.Value);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Managed_rotation_keeps_immutable_history_and_rejects_stale_version_and_duplicate_owner()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepare = Services.GetRequiredService<PrepareManagedSecret>();
        var first = Assert.IsType<Ok<PreparedManagedSecret>>((await prepare(group.TenantId, Id(), owner, "rotate-managed", 1,
            new Dictionary<string, string> { ["username"] = "user", ["password"] = "first" }, ManagedSecretKubernetesType.BasicAuthentication, default)).Value).Value;
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<Ok<Unit>>((await Services.GetRequiredService<CreateBoundManagedSecret>()(first,
            group.Id, group.DefaultIsolationScopeId, deployment.Id, session, default)).Value);
        await session.CommitTransactionAsync();
        var replacement = Assert.IsType<Ok<PreparedManagedSecret>>((await prepare(group.TenantId, first.SecretId, owner, first.Name, 2,
            new Dictionary<string, string> { ["username"] = "user", ["password"] = "second" }, ManagedSecretKubernetesType.BasicAuthentication, default)).Value).Value;
        session.StartTransaction();
        Assert.IsType<Ok<Unit>>((await Services.GetRequiredService<RotateManagedSecret>()(replacement, 1, session, default)).Value);
        await session.CommitTransactionAsync();
        session.StartTransaction();
        var stale = await Services.GetRequiredService<RotateManagedSecret>()(replacement, 1, session, default);
        Assert.IsType<ManagedSecretChanged>(Assert.IsType<Err<ManagedSecretError>>(stale.Value).Value.Value);
        await session.AbortTransactionAsync();
        Assert.Equal(2, await Fixture.Database.GetCollection<SecretVersion>("SecretVersions").CountDocumentsAsync(x => x.SecretId == first.SecretId));
        var read = Assert.IsType<Ok<ManagedSecretValues>>((await Services.GetRequiredService<ReadManagedSecret>()(group.TenantId, owner, default)).Value).Value;
        Assert.Equal("second", read.Values["password"]);
        var username = await Services.GetRequiredService<ReadManagedSecretKey>()(group.TenantId, owner, "username", default);
        Assert.Equal("user", Assert.IsType<Ok<string>>(username.Value).Value);
        Assert.IsType<Err<ManagedSecretError>>((await Services.GetRequiredService<ReadManagedSecretKey>()(group.TenantId, owner, "missing-key", default)).Value);
        var duplicate = Assert.IsType<Ok<PreparedManagedSecret>>((await prepare(group.TenantId, Id(), owner, "duplicate-owner", 1,
            new Dictionary<string, string> { ["password"] = "value" }, ManagedSecretKubernetesType.Opaque, default)).Value).Value;
        session.StartTransaction();
        var duplicateResult = await Services.GetRequiredService<CreateBoundManagedSecret>()(duplicate, group.Id, group.DefaultIsolationScopeId, deployment.Id, session, default);
        Assert.IsType<ManagedSecretChanged>(Assert.IsType<Err<ManagedSecretError>>(duplicateResult.Value).Value.Value);
        await session.AbortTransactionAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Managed_envelope_rejects_changed_context_or_corrupted_ciphertext(bool changeContext)
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepared = Assert.IsType<Ok<PreparedManagedSecret>>((await Services.GetRequiredService<PrepareManagedSecret>()(
            group.TenantId, Id(), owner, "integrity", 1, new Dictionary<string, string> { ["password"] = "private-value" }, ManagedSecretKubernetesType.Opaque, default)).Value).Value;
        if (changeContext)
            prepared = prepared with { SecretId = Id() };
        else
        {
            var envelope = System.Text.Json.JsonSerializer.Deserialize<EncryptedValue>(prepared.EncryptedValues.AsSpan())!;
            envelope.Ciphertext[0] ^= 1;
            prepared = prepared with { EncryptedValues = System.Collections.Immutable.ImmutableArray.CreateRange(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelope)) };
        }
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<Ok<Unit>>((await Services.GetRequiredService<CreateBoundManagedSecret>()(prepared,
            group.Id, group.DefaultIsolationScopeId, deployment.Id, session, default)).Value);
        await session.CommitTransactionAsync();
        var result = await Services.GetRequiredService<ReadManagedSecret>()(group.TenantId, owner, default);
        Assert.IsType<ManagedSecretEncryptionUnavailable>(Assert.IsType<Err<ManagedSecretError>>(result.Value).Value.Value);
        var keyResult = await Services.GetRequiredService<ReadManagedSecretKey>()(group.TenantId, owner, "password", default);
        Assert.IsType<ManagedSecretEncryptionUnavailable>(Assert.IsType<Err<ManagedSecretError>>(keyResult.Value).Value.Value);
    }

    private sealed class UnusedTarget : ISecretProjectionTarget
    {
        public Task<Result<Unit, SecretProjectionTargetError>> ApplyAsync(
            SecretProjection projection,
            Func<CancellationToken, Task<Result<k8s.Models.V1Secret, SecretProjectionTargetError>>> createManifest,
            CancellationToken ct) => throw new InvalidOperationException("Unexpected apply.");
        public Task<Result<Unit, SecretProjectionTargetError>> DeleteAsync(SecretProjection projection, CancellationToken ct) => throw new InvalidOperationException("Unexpected delete.");
    }

    private async Task<ResourceDeployment> StageConsumer(ResourceGroup group, ManagedSecretOwner owner, IClientSessionHandle session)
    {
        Assert.IsType<Ok<RegisteredResource>>((await Services.GetRequiredService<IResourceRegistrar>().RegisterAsync(
            group.TenantId, group.Id, new(ResourceTypeId.Create(owner.ResourceType), owner.ResourceId, "consumer"), session)).Value);
        var now = DateTime.UtcNow;
        var placement = ResolvedResourcePlacement.Create(Id(), TestData.DefaultDeploymentTargetId, "test-namespace", 1, 1,
            ResolvedIngressConfiguration.Create("traefik", "letsencrypt-prod"), null, ResourceDeploymentTestData.Compute(), now);
        var deployment = ResourceDeploymentFactory.Create(group.TenantId, ResourceTypeId.Create(owner.ResourceType), owner.ResourceId,
            group.Id, group.DefaultIsolationScopeId, placement, ResourcePlacementCapability.SingleTarget, true,
            new ResourceDeploymentFactory.CreateOptions(Id(), now));
        await DbContext.ResourceDeployments.InsertOneAsync(session, deployment);
        return deployment;
    }
}
