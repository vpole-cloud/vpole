using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

public sealed partial class ManagedSecretIntegrationTests
{
    [Fact]
    public async Task Owner_purposes_have_independent_credentials_versions_and_protected_bindings()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var admin = new ManagedSecretOwner("pole/postgresql", Id(), "admin");
        var bucket = admin with { Purpose = $"bucket:{Id()}" };
        var otherBucket = admin with { Purpose = $"bucket:{Id()}" };
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, admin, session);
        var first = await CreatePurpose(group, admin, "admin-credentials", deployment.Id, session);
        var second = await CreatePurpose(group, bucket, "bucket-credentials", deployment.Id, session);
        var third = await CreatePurpose(group, otherBucket, "other-bucket-credentials", deployment.Id, session);
        await session.CommitTransactionAsync();

        var read = Services.GetRequiredService<ReadManagedSecret>();
        foreach (var prepared in new[] { first, second, third })
        {
            var values = Assert.IsType<ManagedSecretValues>(
                (await read(group.TenantId, prepared.Owner, default)).Value);
            Assert.Equal(prepared.SecretId, values.SecretId);
            Assert.Equal(prepared.Owner.Purpose, values.Values["password"]);
            var secret = Assert.IsType<Secret>(
                await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, prepared.Owner, default));
            Assert.Equal(prepared.Owner.Purpose, Assert.IsType<ProviderManagedSecret>(secret.Management).Purpose);
            Assert.Equal(prepared.Owner.Purpose, SecretMetadata.From(secret).ManagedBy!.Purpose);
            Assert.IsType<SecretManagedByProvider>(
                (await Services.GetRequiredService<RemoveSecretBinding>()(
                    group.TenantId,
                    secret.Id,
                    Assert.Single(secret.Bindings).Id,
                    default)).Value);
        }

        Assert.IsType<ManagedSecretUnavailable>((await read(Id(), bucket, default)).Value);
        Assert.IsType<ManagedSecretUnavailable>(
            (await read(group.TenantId, bucket with { Purpose = "missing" }, default)).Value);
        Assert.IsType<ManagedSecretUnavailable>(
            (await read(group.TenantId, bucket with { ResourceId = Id() }, default)).Value);
        var key = await Services.GetRequiredService<ReadManagedSecretKey>()(
            new ReadManagedSecretKeyRequest(group.TenantId, bucket, "password"),
            default);
        Assert.Equal(bucket.Purpose, Assert.IsType<ManagedSecretKey>(key.Value).Value);

        var rotated = Assert.IsType<PreparedManagedSecret>(
            (await Services.GetRequiredService<PrepareManagedSecret>()(
                new PrepareManagedSecretRequest(
                    group.TenantId,
                    second.SecretId,
                    bucket,
                    second.Name,
                    2,
                    new Dictionary<string, string> { ["password"] = "bucket-rotated" },
                    ManagedSecretKubernetesType.Opaque,
                    ManagedSecretSharingPolicy.Shareable),
                default)).Value);
        session.StartTransaction();
        Assert.IsType<ManagedSecretChanged>(
            (await Services.GetRequiredService<RotateManagedSecret>()(
                new RotateManagedSecretRequest(rotated with { Owner = admin }, 1),
                session,
                default)).Value);
        await session.AbortTransactionAsync();
        session.StartTransaction();
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<RotateManagedSecret>()(
                new RotateManagedSecretRequest(rotated, 1),
                session,
                default)).Value);
        await session.CommitTransactionAsync();

        Assert.Equal(
            2,
            Assert.IsType<ManagedSecretValues>((await read(group.TenantId, bucket, default)).Value).Version);
        Assert.Equal(
            "bucket-rotated",
            Assert.IsType<ManagedSecretValues>((await read(group.TenantId, bucket, default)).Value).Values["password"]);
        foreach (var unchanged in new[] { first, third })
        {
            var values = Assert.IsType<ManagedSecretValues>(
                (await read(group.TenantId, unchanged.Owner, default)).Value);
            Assert.Equal(1, values.Version);
            Assert.Equal(unchanged.Owner.Purpose, values.Values["password"]);
        }

        var versions = Services.GetRequiredService<IMongoCollection<SecretVersion>>();
        Assert.Equal(2, await versions.CountDocumentsAsync(item => item.SecretId == second.SecretId));
        Assert.Equal(1, await versions.CountDocumentsAsync(item => item.SecretId == first.SecretId));
        Assert.Equal(1, await versions.CountDocumentsAsync(item => item.SecretId == third.SecretId));
    }

    [Fact]
    public async Task Releasing_one_purpose_is_transactional_and_preserves_sibling_credentials()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var admin = new ManagedSecretOwner("pole/postgresql", Id(), "admin");
        var bucket = admin with { Purpose = $"bucket:{Id()}" };
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, admin, session);
        var first = await CreatePurpose(group, admin, "retained-admin", deployment.Id, session);
        var second = await CreatePurpose(group, bucket, "released-bucket", deployment.Id, session);
        await session.CommitTransactionAsync();
        var outboxBefore = await DbContext.CountOutboxMessagesAsync(group.TenantId, "pole.secrets/v1/secret-changed");

        var release = Services.GetRequiredService<ReleaseManagedSecret>();
        session.StartTransaction();
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await release(new ReleaseManagedSecretRequest(group.TenantId, bucket), session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.NotNull(await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, bucket, default));
        Assert.Equal(
            outboxBefore,
            await DbContext.CountOutboxMessagesAsync(group.TenantId, "pole.secrets/v1/secret-changed"));

        session.StartTransaction();
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await release(new ReleaseManagedSecretRequest(Id(), bucket), session, default)).Value);
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await release(
                new ReleaseManagedSecretRequest(group.TenantId, bucket with { Purpose = "missing" }),
                session,
                default)).Value);
        await session.CommitTransactionAsync();
        Assert.NotNull(await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, bucket, default));

        session.StartTransaction();
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await release(new ReleaseManagedSecretRequest(group.TenantId, bucket), session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.Null(await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, bucket, default));
        var preserved = Assert.IsType<ManagedSecretValues>(
            (await Services.GetRequiredService<ReadManagedSecret>()(group.TenantId, admin, default)).Value);
        Assert.Equal(first.SecretId, preserved.SecretId);
        Assert.Equal("admin", preserved.Values["password"]);
        var versions = Services.GetRequiredService<IMongoCollection<SecretVersion>>();
        Assert.Equal(0, await versions.CountDocumentsAsync(item => item.SecretId == second.SecretId));
        Assert.Equal(1, await versions.CountDocumentsAsync(item => item.SecretId == first.SecretId));
    }

    [Fact]
    public async Task Creating_multiple_owner_purposes_rolls_back_every_secret_version_binding_and_event()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var admin = new ManagedSecretOwner("pole/postgresql", Id(), "admin");
        var bucket = admin with { Purpose = $"bucket:{Id()}" };
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, admin, session);
        var first = await CreatePurpose(group, admin, "abort-admin", deployment.Id, session);
        var second = await CreatePurpose(group, bucket, "abort-bucket", deployment.Id, session);
        await session.AbortTransactionAsync();
        var secrets = Services.GetRequiredService<IMongoCollection<Secret>>();
        var versions = Services.GetRequiredService<IMongoCollection<SecretVersion>>();
        foreach (var prepared in new[] { first, second })
        {
            Assert.Null(
                await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, prepared.Owner, default));
            Assert.Equal(0, await secrets.CountDocumentsAsync(item => item.Id == prepared.SecretId));
            Assert.Equal(0, await versions.CountDocumentsAsync(item => item.SecretId == prepared.SecretId));
        }

        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(group.TenantId, "pole.secrets/v1/secret-changed"));
        Assert.Equal(0, await DbContext.ResourceDeployments.CountDocumentsAsync(item => item.Id == deployment.Id));
    }

    [Fact]
    public async Task Owner_only_credentials_reject_generic_sharing_and_sharing_policy_downgrade()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var admin = new ManagedSecretOwner("pole/postgresql", Id(), "admin");
        var consumer = new ManagedSecretOwner("pole/web-service", Id(), ManagedSecretPurposes.Credentials);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, admin, session);
        await StageConsumer(group, consumer, session);
        var prepared = await CreatePurpose(
            group,
            admin,
            "private-admin",
            deployment.Id,
            session,
            ManagedSecretSharingPolicy.OwnerOnly);
        await session.CommitTransactionAsync();

        var attemptedBinding = SecretBinding.Create(
            Id(),
            consumer.ResourceType,
            consumer.ResourceId,
            new AllSecretDeployments(),
            []);
        Assert.IsType<SecretReferenceRejected>(
            (await Services.GetRequiredService<PersistSecretBinding>()(
                group.TenantId,
                prepared.SecretId,
                attemptedBinding,
                default)).Value);
        var original = Assert.IsType<Secret>(
            await Services.GetRequiredService<LoadManagedSecret>()(group.TenantId, admin, default));
        Assert.Single(original.Bindings);
        Assert.False(original.AllowsSharing);
        Assert.True(original.AllowsBinding(original.Bindings[0]));
        Assert.False(original.AllowsBinding(attemptedBinding));
        Assert.IsType<SecretBindingSharingDenied>(original.DeclareBinding(attemptedBinding, DateTime.UtcNow).Value);

        var downgraded = Assert.IsType<PreparedManagedSecret>(
            (await Services.GetRequiredService<PrepareManagedSecret>()(
                new PrepareManagedSecretRequest(
                    group.TenantId,
                    prepared.SecretId,
                    admin,
                    prepared.Name,
                    2,
                    new Dictionary<string, string> { ["password"] = "changed" },
                    ManagedSecretKubernetesType.Opaque,
                    ManagedSecretSharingPolicy.Shareable),
                default)).Value);
        session.StartTransaction();
        Assert.IsType<ManagedSecretChanged>(
            (await Services.GetRequiredService<RotateManagedSecret>()(
                new RotateManagedSecretRequest(downgraded, 1),
                session,
                default)).Value);
        await session.AbortTransactionAsync();
        var values = Assert.IsType<ManagedSecretValues>(
            (await Services.GetRequiredService<ReadManagedSecret>()(group.TenantId, admin, default)).Value);
        Assert.Equal(1, values.Version);
        Assert.Equal("admin", values.Values["password"]);
    }

    [Fact]
    public async Task Asynchronous_owner_cleanup_deletes_private_credentials_instead_of_making_them_shareable()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var admin = new ManagedSecretOwner("pole/postgresql", Id(), "admin");
        var bucket = admin with { Purpose = $"bucket:{Id()}" };
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var deployment = await StageConsumer(group, admin, session);
        var privateCredential = await CreatePurpose(
            group,
            admin,
            "cleanup-admin",
            deployment.Id,
            session,
            ManagedSecretSharingPolicy.OwnerOnly);
        var shareable = await CreatePurpose(group, bucket, "cleanup-bucket", deployment.Id, session);
        await session.CommitTransactionAsync();

        var cleanup = Services.GetRequiredService<RemoveConsumerSecretBindings>();
        await cleanup(group.TenantId, group.Id, admin.ResourceType, admin.ResourceId, default);
        await cleanup(group.TenantId, group.Id, admin.ResourceType, admin.ResourceId, default);
        var secrets = Services.GetRequiredService<IMongoCollection<Secret>>();
        Assert.Equal(0, await secrets.CountDocumentsAsync(item => item.Id == privateCredential.SecretId));
        var versions = Services.GetRequiredService<IMongoCollection<SecretVersion>>();
        Assert.Equal(0, await versions.CountDocumentsAsync(item => item.SecretId == privateCredential.SecretId));
        var retained = await secrets.Find(item => item.Id == shareable.SecretId).SingleAsync();
        Assert.IsType<UnmanagedSecret>(retained.Management);
        Assert.Empty(retained.Bindings);
        Assert.Equal(1, await versions.CountDocumentsAsync(item => item.SecretId == shareable.SecretId));
    }

    private async Task<PreparedManagedSecret> CreatePurpose(
        ResourceGroup group,
        ManagedSecretOwner owner,
        string name,
        string deploymentId,
        IClientSessionHandle session,
        ManagedSecretSharingPolicy sharingPolicy = ManagedSecretSharingPolicy.Shareable)
    {
        var prepared = Assert.IsType<PreparedManagedSecret>(
            (await Services.GetRequiredService<PrepareManagedSecret>()(
                new PrepareManagedSecretRequest(
                    group.TenantId,
                    Id(),
                    owner,
                    name,
                    1,
                    new Dictionary<string, string> { ["password"] = owner.Purpose },
                    ManagedSecretKubernetesType.Opaque,
                    sharingPolicy),
                default)).Value);
        Assert.IsType<ManagedSecretMutationSucceeded>(
            (await Services.GetRequiredService<CreateBoundManagedSecret>()(
                new CreateBoundManagedSecretRequest(prepared, group.Id, group.DefaultIsolationScopeId, deploymentId),
                session,
                default)).Value);
        return prepared;
    }
}
