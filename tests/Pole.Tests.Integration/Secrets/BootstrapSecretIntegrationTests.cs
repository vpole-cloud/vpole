using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

public sealed partial class ManagedSecretIntegrationTests
{
    [Fact]
    public async Task Bootstrap_copies_only_granted_key_once_and_ignores_later_source_rotation()
    {
        var request = await PrepareBootstrapAsync(SecretBindingUsage.Bootstrap);
        using (var session = await Fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            var plan = Assert.IsType<BootstrapSecretReady>(
                (await Services.GetRequiredService<ReadBootstrapSecret>()(request, session, default)).Value);
            Assert.DoesNotContain("initial-private-value", plan.ToString());
            Assert.IsType<BootstrapSecretInitialized>(
                (await Services.GetRequiredService<CreateBootstrapSecret>()(plan, session, default)).Value);
            await session.CommitTransactionAsync();
        }

        Assert.IsType<Secret>(
            (await Services.GetRequiredService<PersistRotatedSecret>()(
                request.TenantId,
                request.Source.SecretId,
                1,
                new Dictionary<string, string> { ["password"] = "later-private-value" },
                default)).Value);
        using (var session = await Fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            var repeated = Assert.IsType<BootstrapSecretInitialized>(
                (await Services.GetRequiredService<ReadBootstrapSecret>()(request, session, default)).Value);
            Assert.Equal(request.CredentialId, repeated.CredentialId);
            await session.CommitTransactionAsync();
        }

        var owned = Assert.IsType<ManagedSecretValues>(
            (await Services.GetRequiredService<ReadManagedSecret>()(request.TenantId, request.Owner, default)).Value);
        Assert.Equal("initial-private-value", owned.Values["password"]);
        Assert.Equal("database-user", owned.Values["username"]);
        Assert.Equal(1, owned.Version);
        Assert.Equal(
            1,
            await Fixture.Database
                .GetCollection<SecretVersion>("SecretVersions")
                .CountDocumentsAsync(item => item.SecretId == request.CredentialId));
    }

    [Theory]
    [InlineData(SecretBindingUsage.Runtime)]
    [InlineData(SecretBindingUsage.Bootstrap)]
    public async Task Bootstrap_requires_specific_grant_and_current_tenant(SecretBindingUsage usage)
    {
        var request = await PrepareBootstrapAsync(usage);
        if (usage == SecretBindingUsage.Bootstrap)
            request = request with { TenantId = Id() };
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var rejected = Assert.IsType<BootstrapSecretUnavailable>(
            (await Services.GetRequiredService<ReadBootstrapSecret>()(request, session, default)).Value);
        Assert.Equal(ConsumerSecretReferenceError.SecretBindingUnavailable, rejected.Reason);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task Bootstrap_rejects_changed_source_and_rolls_back_created_credential_on_abort()
    {
        var request = await PrepareBootstrapAsync(SecretBindingUsage.Bootstrap);
        BootstrapSecretReady plan;
        using (var session = await Fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            plan = Assert.IsType<BootstrapSecretReady>(
                (await Services.GetRequiredService<ReadBootstrapSecret>()(request, session, default)).Value);
            await session.AbortTransactionAsync();
        }

        Assert.IsType<Secret>(
            (await Services.GetRequiredService<PersistRotatedSecret>()(
                request.TenantId,
                request.Source.SecretId,
                1,
                new Dictionary<string, string> { ["password"] = "concurrent-value" },
                default)).Value);
        using (var session = await Fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            Assert.IsType<BootstrapSecretChanged>(
                (await Services.GetRequiredService<CreateBootstrapSecret>()(plan, session, default)).Value);
            var refreshed = Assert.IsType<BootstrapSecretReady>(
                (await Services.GetRequiredService<ReadBootstrapSecret>()(request, session, default)).Value);
            Assert.IsType<BootstrapSecretInitialized>(
                (await Services.GetRequiredService<CreateBootstrapSecret>()(refreshed, session, default)).Value);
            await session.AbortTransactionAsync();
        }

        Assert.Null(await Services.GetRequiredService<LoadManagedSecret>()(request.TenantId, request.Owner, default));
        Assert.Equal(
            0,
            await Fixture.Database
                .GetCollection<SecretVersion>("SecretVersions")
                .CountDocumentsAsync(item => item.SecretId == request.CredentialId));
    }

    private async Task<BootstrapSecretRequest> PrepareBootstrapAsync(SecretBindingUsage usage)
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var owner = new ManagedSecretOwner("pole/postgresql", Id());
        string deploymentId;
        using (var session = await Fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            deploymentId = (await StageConsumer(group, owner, session)).Id;
            await session.CommitTransactionAsync();
        }

        var source = Assert.IsType<Secret>(
            (await Services.GetRequiredService<PersistCreatedSecret>()(
                group.TenantId,
                group.Id,
                group.DefaultIsolationScopeId,
                "bootstrap-input",
                "test",
                new Dictionary<string, string> { ["password"] = "initial-private-value" },
                default)).Value);
        var binding = SecretBinding.Create(
            Id(),
            owner.ResourceType,
            owner.ResourceId,
            new AllSecretDeployments(),
            new Dictionary<string, string> { ["password"] = "password" },
            usage);
        Assert.IsType<Secret>(
            (await Services.GetRequiredService<PersistSecretBinding>()(group.TenantId, source.Id, binding, default))
            .Value);
        return new BootstrapSecretRequest(
            group.TenantId,
            owner,
            new BootstrapSecretSource(source.Id, "password"),
            new BootstrapSecretPlacement(group.Id, group.DefaultIsolationScopeId, deploymentId),
            Id(),
            "owned-credentials",
            "database-user");
    }
}
