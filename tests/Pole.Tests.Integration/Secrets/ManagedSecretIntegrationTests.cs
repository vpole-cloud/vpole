// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.GitOps.Application;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Migrations;
using Pole.Migrations;
using Pole.Platform.Hosting;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

[Collection(IntegrationTestCollection.Tenant4)]
public sealed partial class ManagedSecretIntegrationTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private static string Id() => ObjectId.GenerateNewId().ToString();

    [Fact]
    public async Task Staged_owner_placement_and_bound_secret_commit_atomically_and_protect_provider_mutations()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepared = Assert.IsType<PreparedManagedSecret>(
            (await Services.GetRequiredService<PrepareManagedSecret>()(
                new PrepareManagedSecretRequest(
                    TenantId: group.TenantId,
                    SecretId: Id(),
                    Owner: owner,
                    Name: "database-credentials",
                    Version: 1,
                    Values: new Dictionary<string, string> { ["password"] = "private-value" },
                    KubernetesType: ManagedSecretKubernetesType.Opaque),
                ct: default)).Value);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<CreateBoundManagedSecret>()(
                new CreateBoundManagedSecretRequest(
                    Prepared: prepared,
                    ResourceGroupId: group.Id,
                    IsolationScopeId: group.DefaultIsolationScopeId,
                    DeploymentId: deployment.Id),
                session: session,
                ct: default)).Value);
        await session.CommitTransactionAsync();
        var secret = Assert.IsType<Secret>(
            await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, owner, default));
        Assert.Single(secret.Bindings);
        Assert.IsType<SecretManagedByProvider>(
            (await Services.GetRequiredService<RemoveSecretBinding>()(
                group.TenantId,
                secret.Id,
                secret.Bindings[0].Id,
                default)).Value);
        Assert.IsType<SecretManagedByProvider>(
            (await Services.GetRequiredService<PersistRotatedSecret>()(
                group.TenantId,
                secret.Id,
                1,
                new Dictionary<string, string> { ["password"] = "bypass" },
                default)).Value);
        var values = Assert.IsType<ManagedSecretValues>(
            (await Services.GetRequiredService<ReadManagedSecret>()(group.TenantId, owner, default)).Value);
        Assert.Equal("private-value", values.Values["password"]);
        var canonicalRead = await Services.GetRequiredService<ReadManagedSecret>()(
            group.TenantId.ToUpperInvariant(),
            owner with
            {
                ResourceId = owner.ResourceId.ToUpperInvariant(),
            },
            default);
        Assert.Equal("private-value", Assert.IsType<ManagedSecretValues>(canonicalRead.Value).Values["password"]);
        Assert.DoesNotContain("private-value", values.ToString());
        Assert.IsType<ManagedSecretUnavailable>(
            (await Services.GetRequiredService<ReadManagedSecret>()(Id(), owner, default)).Value);
        await Services.GetRequiredService<RemoveConsumerSecretBindings>()(
            group.TenantId,
            group.Id,
            owner.ResourceType,
            owner.ResourceId,
            default);
        var retained = Assert.IsType<Secret>(
            await Services.GetRequiredService<LoadSecretById>()(group.TenantId, secret.Id, default));
        Assert.IsType<UnmanagedSecret>(retained.Management);
        Assert.Empty(retained.Bindings);
        Assert.IsType<Secret>(
            (await Services.GetRequiredService<PersistRotatedSecret>()(
                group.TenantId,
                retained.Id,
                1,
                new Dictionary<string, string> { ["password"] = "fallback-rotated" },
                default)).Value);
    }

    [Fact]
    public async Task Aborting_owner_creation_removes_encrypted_secret_binding_registration_and_outbox()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepared = Assert.IsType<PreparedManagedSecret>(
            (await Services.GetRequiredService<PrepareManagedSecret>()(
                new PrepareManagedSecretRequest(
                    TenantId: group.TenantId,
                    SecretId: Id(),
                    Owner: owner,
                    Name: "abort-credentials",
                    Version: 1,
                    Values: new Dictionary<string, string> { ["password"] = "private-value" },
                    KubernetesType: ManagedSecretKubernetesType.Opaque),
                ct: default)).Value);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<CreateBoundManagedSecret>()(
                new CreateBoundManagedSecretRequest(
                    Prepared: prepared,
                    ResourceGroupId: group.Id,
                    IsolationScopeId: group.DefaultIsolationScopeId,
                    DeploymentId: deployment.Id),
                session: session,
                ct: default)).Value);
        await session.AbortTransactionAsync();
        Assert.Null(await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, owner, default));
        Assert.Equal(
            0,
            await Fixture.Database
                .GetCollection<SecretVersion>("SecretVersions")
                .CountDocumentsAsync(x => x.SecretId == prepared.SecretId));
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(Data.TenantId, "pole.secrets/v1/secret-changed"));
        Assert.Equal(0, await DbContext.ResourceDeployments.CountDocumentsAsync(x => x.Id == deployment.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_release_deletes_unshared_credentials_but_retains_shared_secret_as_unmanaged(bool shared)
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepared = Assert.IsType<PreparedManagedSecret>(
            (await Services.GetRequiredService<PrepareManagedSecret>()(
                new PrepareManagedSecretRequest(
                    TenantId: group.TenantId,
                    SecretId: Id(),
                    Owner: owner,
                    Name: "shared-credentials",
                    Version: 1,
                    Values: new Dictionary<string, string> { ["password"] = "private-value" },
                    KubernetesType: ManagedSecretKubernetesType.Opaque),
                ct: default)).Value);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<CreateBoundManagedSecret>()(
                new CreateBoundManagedSecretRequest(
                    Prepared: prepared,
                    ResourceGroupId: group.Id,
                    IsolationScopeId: group.DefaultIsolationScopeId,
                    DeploymentId: deployment.Id),
                session: session,
                ct: default)).Value);
        var consumer = new ManagedSecretOwner("pole/web-service", Id());
        await StageConsumer(group, consumer, session);
        await session.CommitTransactionAsync();
        if (shared)
            Assert.IsType<Secret>(
                (await Services.GetRequiredService<PersistSecretBinding>()(
                    group.TenantId,
                    prepared.SecretId,
                    SecretBinding.Create(
                        Id(),
                        consumer.ResourceType,
                        consumer.ResourceId,
                        new AllSecretDeployments(),
                        []),
                    default)).Value);
        session.StartTransaction();
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<ReleaseManagedSecret>()(
                new ReleaseManagedSecretRequest(TenantId: group.TenantId, Owner: owner),
                session: session,
                ct: default)).Value);
        await session.CommitTransactionAsync();
        var secret = await Services.GetRequiredService<LoadSecretById>()(group.TenantId, prepared.SecretId, default);
        if (shared)
        {
            Assert.NotNull(secret);
            Assert.IsType<UnmanagedSecret>(secret.Management);
            Assert.Equal(consumer.ResourceId, Assert.Single(secret.Bindings).Consumer.ResourceId);
            Assert.IsType<Secret>(
                (await Services.GetRequiredService<PersistRotatedSecret>()(
                    group.TenantId,
                    secret.Id,
                    1,
                    new Dictionary<string, string> { ["password"] = "now-ordinary" },
                    default)).Value);
        }
        else
        {
            Assert.Null(secret);
            Assert.Equal(
                0,
                await Fixture.Database
                    .GetCollection<SecretVersion>("SecretVersions")
                    .CountDocumentsAsync(x => x.SecretId == prepared.SecretId));
        }
    }

    [Fact]
    public async Task Owner_cannot_resolve_another_consumers_projection_from_shared_secret()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepared = Assert.IsType<PreparedManagedSecret>(
            (await Services.GetRequiredService<PrepareManagedSecret>()(
                new PrepareManagedSecretRequest(
                    TenantId: group.TenantId,
                    SecretId: Id(),
                    Owner: owner,
                    Name: "owner-credentials",
                    Version: 1,
                    Values: new Dictionary<string, string> { ["password"] = "value" },
                    KubernetesType: ManagedSecretKubernetesType.Opaque),
                ct: default)).Value);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<CreateBoundManagedSecret>()(
                new CreateBoundManagedSecretRequest(
                    Prepared: prepared,
                    ResourceGroupId: group.Id,
                    IsolationScopeId: group.DefaultIsolationScopeId,
                    DeploymentId: deployment.Id),
                session: session,
                ct: default)).Value);
        await session.CommitTransactionAsync();
        var secret = Assert.IsType<Secret>(
            await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, owner, default));
        var otherBinding = SecretBinding.Create(Id(), "pole/web-service", Id(), new AllSecretDeployments(), []);
        var other = SecretProjectionFactory.Create(secret, otherBinding, Id(), "cell", "target", "namespace").Applied();
        await Fixture.Database.GetCollection<SecretProjection>("SecretProjections").InsertOneAsync(other);
        var collection = new ServiceCollection().AddGitOpsFeature().AddSecretsFeature();
        collection.AddSingleton(Fixture.Database);
        collection.AddSingleton<IMongoClient>(Fixture.Client);
        collection.AddSingleton<MongoRunTransaction>();
        collection.AddSingleton(Fixture.StoreOutboxWrites);
        collection.AddSingleton(TimeProvider.System);
        collection.AddSingleton(Services.GetRequiredService<IEnvelopeEncryptionService>());
        collection.AddSingleton<LoadResourceDeploymentSet>((request, _) =>
            Task.FromResult<IReadOnlyList<PlacementSummaryDto>>([]));
        collection.AddSingleton<LoadExactResourceDeploymentRuntimePlacement>((request, _) =>
            Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(null));
        collection.AddSingleton<ClaimSecretProjection>((
            _, _,
            _, _) => Task.FromResult<SecretProjection?>(null));
        collection.AddSingleton<ISecretProjectionTarget>(new UnusedTarget());
        using var isolated = collection.BuildServiceProvider();
        var result = await isolated.GetRequiredService<EnsureManagedSecretProjection>()(
            new EnsureManagedSecretProjectionRequest(
                TenantId: group.TenantId,
                Owner: owner,
                DeploymentId: other.DeploymentId),
            ct: default);
        Assert.IsType<ManagedSecretUnavailable>(result.Value);
    }

    [Fact]
    public async Task Owner_recreation_cannot_reuse_previous_secret_projection_identity()
    {
        var binding = SecretBinding.Create(Id(), "pole/postgresql", Id(), new AllSecretDeployments(), []);
        var original = SecretFactory.CreateManaged(
            Id(),
            Id(),
            Id(),
            Id(),
            "owner-credentials",
            "credentials",
            ["password"],
            binding,
            KubernetesSecretType.Opaque,
            DateTime.UtcNow);
        // Preserve all other metadata to prove identity itself is fenced, independently of binding/version checks.
        var recreated = original with
        {
            Id = Id(),
        };
        var projection = SecretProjectionFactory.Create(original, binding, Id(), "cell", "target", "namespace")
            .Applied();
        var loads = 0;
        var collection = new ServiceCollection().AddGitOpsFeature().AddSecretsFeature();
        collection.AddSingleton(Fixture.Database);
        collection.AddSingleton<IMongoClient>(Fixture.Client);
        collection.AddSingleton<MongoRunTransaction>();
        collection.AddSingleton(Fixture.StoreOutboxWrites);
        collection.AddSingleton(TimeProvider.System);
        collection.AddSingleton(Services.GetRequiredService<IEnvelopeEncryptionService>());
        collection.AddSingleton<LoadManagedSecret>((
            _, _,
            _) => Task.FromResult<Secret?>(++loads == 1 ? original : recreated));
        collection.AddSingleton<LoadSecretById>((
            _, _,
            _) => Task.FromResult<Secret?>(null));
        collection.AddSingleton<LoadSecretProjections>((
            _, _,
            _) => Task.FromResult<IReadOnlyList<SecretProjection>>([projection]));
        collection.AddSingleton<LoadResourceDeploymentSet>((request, _) =>
            Task.FromResult<IReadOnlyList<PlacementSummaryDto>>([]));
        collection.AddSingleton<LoadExactResourceDeploymentRuntimePlacement>((request, _) =>
            Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(null));
        collection.AddSingleton<ClaimSecretProjection>((
            _, _,
            _, _) => Task.FromResult<SecretProjection?>(null));
        collection.AddSingleton<ISecretProjectionTarget>(new UnusedTarget());
        using var isolated = collection.BuildServiceProvider();
        var result = await isolated.GetRequiredService<EnsureManagedSecretProjection>()(
            new EnsureManagedSecretProjectionRequest(
                TenantId: original.TenantId,
                Owner: new ManagedSecretOwner(binding.Consumer.ResourceType, binding.Consumer.ResourceId),
                DeploymentId: projection.DeploymentId),
            ct: default);
        Assert.IsType<ManagedSecretUnavailable>(result.Value);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Managed_rotation_keeps_immutable_history_and_rejects_stale_version_and_duplicate_owner()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepare = Services.GetRequiredService<PrepareManagedSecret>();
        var first = Assert.IsType<PreparedManagedSecret>(
            (await prepare(
                new PrepareManagedSecretRequest(
                    TenantId: group.TenantId,
                    SecretId: Id(),
                    Owner: owner,
                    Name: "rotate-managed",
                    Version: 1,
                    Values: new Dictionary<string, string> { ["username"] = "user", ["password"] = "first" },
                    KubernetesType: ManagedSecretKubernetesType.BasicAuthentication),
                ct: default)).Value);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<CreateBoundManagedSecret>()(
                new CreateBoundManagedSecretRequest(
                    Prepared: first,
                    ResourceGroupId: group.Id,
                    IsolationScopeId: group.DefaultIsolationScopeId,
                    DeploymentId: deployment.Id),
                session: session,
                ct: default)).Value);
        await session.CommitTransactionAsync();
        var replacement = Assert.IsType<PreparedManagedSecret>(
            (await prepare(
                new PrepareManagedSecretRequest(
                    TenantId: group.TenantId,
                    SecretId: first.SecretId,
                    Owner: owner,
                    Name: first.Name,
                    Version: 2,
                    Values: new Dictionary<string, string> { ["username"] = "user", ["password"] = "second" },
                    KubernetesType: ManagedSecretKubernetesType.BasicAuthentication),
                ct: default)).Value);
        session.StartTransaction();
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<RotateManagedSecret>()(
                new RotateManagedSecretRequest(Prepared: replacement, ExpectedVersion: 1),
                session: session,
                ct: default)).Value);
        await session.CommitTransactionAsync();
        session.StartTransaction();
        var stale = await Services.GetRequiredService<RotateManagedSecret>()(
            new RotateManagedSecretRequest(Prepared: replacement, ExpectedVersion: 1),
            session: session,
            ct: default);
        Assert.IsType<ManagedSecretChanged>(stale.Value);
        await session.AbortTransactionAsync();
        Assert.Equal(
            2,
            await Fixture.Database
                .GetCollection<SecretVersion>("SecretVersions")
                .CountDocumentsAsync(x => x.SecretId == first.SecretId));
        var read = Assert.IsType<ManagedSecretValues>(
            (await Services.GetRequiredService<ReadManagedSecret>()(group.TenantId, owner, default)).Value);
        Assert.Equal("second", read.Values["password"]);
        var username = await Services.GetRequiredService<ReadManagedSecretKey>()(
            new ReadManagedSecretKeyRequest(TenantId: group.TenantId, Owner: owner, Key: "username"),
            ct: default);
        Assert.Equal("user", Assert.IsType<ManagedSecretKey>(username.Value).Value);
        Assert.IsType<ManagedSecretUnavailable>(
            (await Services.GetRequiredService<ReadManagedSecretKey>()(
                new ReadManagedSecretKeyRequest(TenantId: group.TenantId, Owner: owner, Key: "missing-key"),
                ct: default)).Value);
        var duplicate = Assert.IsType<PreparedManagedSecret>(
            (await prepare(
                new PrepareManagedSecretRequest(
                    TenantId: group.TenantId,
                    SecretId: Id(),
                    Owner: owner,
                    Name: "duplicate-owner",
                    Version: 1,
                    Values: new Dictionary<string, string> { ["password"] = "value" },
                    KubernetesType: ManagedSecretKubernetesType.Opaque),
                ct: default)).Value);
        session.StartTransaction();
        var duplicateResult = await Services.GetRequiredService<CreateBoundManagedSecret>()(
            new CreateBoundManagedSecretRequest(
                Prepared: duplicate,
                ResourceGroupId: group.Id,
                IsolationScopeId: group.DefaultIsolationScopeId,
                DeploymentId: deployment.Id),
            session: session,
            ct: default);
        Assert.IsType<ManagedSecretChanged>(duplicateResult.Value);
        await session.AbortTransactionAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Managed_envelope_rejects_changed_context_or_corrupted_ciphertext(bool changeContext)
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        var prepared = Assert.IsType<PreparedManagedSecret>(
            (await Services.GetRequiredService<PrepareManagedSecret>()(
                new PrepareManagedSecretRequest(
                    TenantId: group.TenantId,
                    SecretId: Id(),
                    Owner: owner,
                    Name: "integrity",
                    Version: 1,
                    Values: new Dictionary<string, string> { ["password"] = "private-value" },
                    KubernetesType: ManagedSecretKubernetesType.Opaque),
                ct: default)).Value);
        if (changeContext)
        {
            prepared = prepared with
            {
                SecretId = Id(),
            };
        }
        else
        {
            var envelope = JsonSerializer.Deserialize<EncryptedValue>(prepared.EncryptedValues.AsSpan())!;
            envelope.Ciphertext[0] ^= 1;
            prepared = prepared with
            {
                EncryptedValues = ImmutableArray.CreateRange(JsonSerializer.SerializeToUtf8Bytes(envelope)),
            };
        }

        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, owner, session);
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<CreateBoundManagedSecret>()(
                new CreateBoundManagedSecretRequest(
                    Prepared: prepared,
                    ResourceGroupId: group.Id,
                    IsolationScopeId: group.DefaultIsolationScopeId,
                    DeploymentId: deployment.Id),
                session: session,
                ct: default)).Value);
        await session.CommitTransactionAsync();
        var result = await Services.GetRequiredService<ReadManagedSecret>()(group.TenantId, owner, default);
        Assert.IsType<ManagedSecretEncryptionUnavailable>(result.Value);
        var keyResult = await Services.GetRequiredService<ReadManagedSecretKey>()(
            new ReadManagedSecretKeyRequest(TenantId: group.TenantId, Owner: owner, Key: "password"),
            ct: default);
        Assert.IsType<ManagedSecretEncryptionUnavailable>(keyResult.Value);
    }

    private async Task<ResourceDeployment> StageConsumer(
        ResourceGroup group,
        ManagedSecretOwner owner,
        IClientSessionHandle session)
    {
        Assert.IsType<RegisteredResource>(
            (await Services.GetRequiredService<IResourceRegistrar>()
                .RegisterAsync(
                    new RegisterResourceRequest(
                        TenantId: group.TenantId,
                        ResourceGroupId: group.Id,
                        Resource: new ResourceRegistrationDescriptor(
                            ResourceTypeId.Create(owner.ResourceType),
                            owner.ResourceId,
                            "consumer")),
                    session: session)).Value);
        var now = DateTime.UtcNow;
        var placement = ResolvedResourcePlacement.Create(
            Id(),
            TestData.DefaultDeploymentTargetId,
            "test-namespace",
            1,
            1,
            ResolvedIngressConfiguration.Create("traefik", "letsencrypt-prod"),
            null,
            ResourceDeploymentTestData.Compute(),
            now);
        var deployment = ResourceDeploymentFactory.Create(
            group.TenantId,
            ResourceTypeId.Create(owner.ResourceType),
            owner.ResourceId,
            group.Id,
            group.DefaultIsolationScopeId,
            placement,
            ResourcePlacementCapability.SingleTarget,
            true,
            ResourceDeploymentFactory.DefaultOptions with
            {
                Id = Id(),
                CreatedAtUtc = now,
            });
        await DbContext.ResourceDeployments.InsertOneAsync(session, deployment);
        return deployment;
    }

    private sealed class UnusedTarget : ISecretProjectionTarget
    {
        public Task<SecretProjectionTargetOutcome> ApplyAsync(
            SecretProjection projection,
            Func<CancellationToken, Task<SecretProjectionManifestOutcome>> createManifest,
            CancellationToken ct) => throw new InvalidOperationException("Unexpected apply.");

        public Task<SecretProjectionTargetOutcome> DeleteAsync(SecretProjection projection, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected delete.");
    }
}

public sealed class SecretMigrationCompositionTests
{
    [Fact]
    public void PlatformCompositionRegistersTheSecretProjectionMigration()
    {
        var services = new ServiceCollection().AddPolePlatform(new ConfigurationBuilder().Build(), [], [], []);
        using var production = services.BuildServiceProvider();
        Assert.Single(production.GetServices<IMigration>().OfType<SecretProjectionIndexesMigration>());
    }
}
