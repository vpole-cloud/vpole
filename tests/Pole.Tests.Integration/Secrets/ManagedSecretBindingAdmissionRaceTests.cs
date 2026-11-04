using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
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
    [Fact]
    public async Task New_binding_after_empty_guard_conflicts_with_consumer_deletion_fence()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/web-service", Id());
        using var setup = await Fixture.Client.StartSessionAsync();
        setup.StartTransaction();
        await StageConsumer(group, owner, setup);
        await setup.CommitTransactionAsync();
        var type = ResourceTypeId.Create(owner.ResourceType);
        var secret = SecretFactory.CreateMetadata(Id(), group.TenantId, group.Id, group.DefaultIsolationScopeId,
            "late-secret", "race", [], DateTime.UtcNow);
        await Services.GetRequiredService<IMongoCollection<Secret>>().InsertOneAsync(secret);
        using var deletion = await Fixture.Client.StartSessionAsync();
        deletion.StartTransaction();
        var guard = Services.GetRequiredService<EnsureConsumerSecretBindingRemoval>();
        Assert.IsType<ConsumerSecretBindingsRemovable>((await guard(new(group.TenantId, type, owner.ResourceId),
            deletion, default)).Value);
        var binding = SecretBinding.Create(Id(), type.Value, owner.ResourceId, new AllSecretDeployments(), []) with
        { DeclarationName = "late-binding" };
        Assert.IsType<Secret>((await Services.GetRequiredService<PersistSecretBinding>()(group.TenantId,
            secret.Id, binding, default)).Value);
        await OwnBinding(group.TenantId, binding);
        var fence = Services.GetRequiredService<FenceRegisteredResource>();
        var conflict = await Assert.ThrowsAsync<MongoCommandException>(() => fence(
            new(group.TenantId, type, owner.ResourceId), deletion, default));
        Assert.True(conflict.HasErrorLabel("TransientTransactionError"));
        await deletion.AbortTransactionAsync();
        deletion.StartTransaction();
        Assert.IsType<ConsumerSecretBindingsRequireRemoval>((await guard(new(group.TenantId, type, owner.ResourceId),
            deletion, default)).Value);
        await deletion.AbortTransactionAsync();
        Assert.Single((await Services.GetRequiredService<IMongoCollection<Secret>>()
            .Find(item => item.Id == secret.Id).SingleAsync()).Bindings);
    }

}
