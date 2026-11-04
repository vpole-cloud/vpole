using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

public sealed partial class ManagedSecretIntegrationTests
{
    [Theory]
    [InlineData("pole/web-service")]
    [InlineData("pole/function-app")]
    [InlineData("pole/postgresql")]
    [InlineData("pole/redis")]
    public async Task Consumer_deletion_guard_rejects_an_external_source_binding_without_writes(string kind)
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var type = ResourceTypeId.Create(kind);
        var consumer = Id();
        var secret = await ConsumerSecretDeletionTestData.InsertAsync(Fixture.Database, group, type, consumer);
        await OwnBinding(secret.TenantId, secret.Bindings.Single());
        var guard = Services.GetRequiredService<EnsureConsumerSecretBindingRemoval>();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var result = await guard(new(group.TenantId, type, consumer), session, default);
        Assert.Equal(1, Assert.IsType<ConsumerSecretBindingsRequireRemoval>(result.Value).BindingCount);
        Assert.IsType<ConsumerSecretBindingsRemovable>((await guard(new(Id(), type, consumer),
            session, default)).Value);
        Assert.IsType<ConsumerSecretBindingsRemovable>((await guard(new(group.TenantId,
            ResourceTypeId.Create("other/type"), consumer), session, default)).Value);
        await session.CommitTransactionAsync();
        var stored = await Services.GetRequiredService<IMongoCollection<Secret>>()
            .Find(item => item.Id == secret.Id).SingleAsync();
        Assert.Equal(secret.ToBsonDocument(), stored.ToBsonDocument());
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync("pole.secrets/v1/secret-changed"));
    }

    [Fact]
    public async Task Unowned_provider_credential_binding_is_allowed_and_secret_fence_is_transactional()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var type = ResourceTypeId.Create("pole/postgresql");
        var consumer = Id();
        var secret = await ConsumerSecretDeletionTestData.InsertAsync(Fixture.Database, group, type, consumer);
        secret = secret with
        {
            Management = new ProviderManagedSecret(type.Value, consumer,
            secret.Bindings.Single().Id, KubernetesSecretType.BasicAuthentication)
        };
        var secrets = Services.GetRequiredService<IMongoCollection<Secret>>();
        await secrets.ReplaceOneAsync(item => item.Id == secret.Id, secret);
        secret = await secrets.Find(item => item.Id == secret.Id).SingleAsync();
        var guard = Services.GetRequiredService<EnsureConsumerSecretBindingRemoval>();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<ConsumerSecretBindingsRemovable>((await guard(new(group.TenantId, type, consumer),
            session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(secret.ReferenceFence, (await secrets.Find(item => item.Id == secret.Id).SingleAsync())
            .ReferenceFence);
        session.StartTransaction();
        Assert.IsType<ConsumerSecretBindingsRemovable>((await guard(new(group.TenantId, type, consumer),
            session, default)).Value);
        await session.CommitTransactionAsync();
        var stored = await secrets.Find(item => item.Id == secret.Id).SingleAsync();
        Assert.Equal(secret.ReferenceFence + 1, stored.ReferenceFence);
        Assert.Equal(secret.Revision, stored.Revision);
        Assert.Equal(secret.UpdatedAtUtc, stored.UpdatedAtUtc);
        Assert.Single(stored.Bindings);
    }

    private Task OwnBinding(string tenant, SecretBinding binding) => Fixture.Database
        .GetCollection<ManagedDeclaration>("ManagedDeclarations").InsertOneAsync(new(
            $"{tenant}/SecretBinding/{binding.DeclarationName}", tenant, "SecretBinding", binding.DeclarationName!,
            Id(), "https://example.test/another-source", binding.Id, "hash", "hash", null, false, Id(), "Bind", 0,
            GitResourceMaterialization.Created, GitManagedProjectionsFactory.Empty()));
}
