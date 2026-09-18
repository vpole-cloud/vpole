using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Encryption;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Migrations;
using Pole.Features.Secrets.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

public sealed class SecretIntegrationTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private SecretStore Store => Services.GetRequiredService<SecretStore>();
    private IMongoCollection<SecretVersion> Versions => Fixture.Database.GetCollection<SecretVersion>("SecretVersions");
    private static string Id() => ObjectId.GenerateNewId().ToString();
    private static Dictionary<string, string> Values(string value = "synthetic-password") => new() { ["password"] = value };
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new SecretIndexesMigration().Up(Fixture.Database);
    }

    [Fact]
    public async Task CreateRotate_ReadsOnlyMetadata_StoresEncryptedImmutableVersions_AndFencesTenants()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = Metadata(await Store.CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId, "credentials", "tests", Values(), default));
        var first = await Versions.Find(x => x.SecretId == created.Id).SingleAsync();
        Assert.DoesNotContain("synthetic-password", first.ToJson());
        Assert.DoesNotContain("Ciphertext", JsonSerializer.Serialize(created));
        var decrypted = await Services.GetRequiredService<IEnvelopeEncryptionService>().DecryptAsync(first.Value,
            EnvelopeEncryptionContext.ForTenant(group.TenantId, "Secret", created.Id, "values", 1));
        Assert.True(decrypted is Ok<byte[]>);
        Assert.Null(await Store.GetAsync(Id(), created.Id, default));
        AssertError<SecretMissing>(await Store.RotateAsync(Id(), created.Id, 1, Values(), default));
        var rotated = Metadata(await Store.RotateAsync(group.TenantId, created.Id, 1, Values("rotated-password"), default));
        Assert.Equal(2, rotated.CurrentVersion);
        Assert.Equal(2, await Versions.CountDocumentsAsync(x => x.SecretId == created.Id));
        Assert.Equal(first.ToJson(), (await Versions.Find(x => x.Id == first.Id).SingleAsync()).ToJson());
        AssertError<SecretConflict>(await Store.RotateAsync(group.TenantId, created.Id, 1, Values(), default));
        AssertError<SecretConflict>(await Store.CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId, " CREDENTIALS ", "tests", Values(), default));
        var outbox = await DbContext.OutboxMessages.Find(Builders<Pole.Features.Outbox.Application.Model.OutboxMessage>.Filter.Empty).ToListAsync();
        Assert.DoesNotContain("synthetic-password", outbox.ToJson());
        Assert.DoesNotContain("rotated-password", outbox.ToJson());
        Assert.DoesNotContain("Ciphertext", outbox.ToJson());
    }

    [Fact]
    public async Task BindingsShareOnlyWithinScope_RejectForeignDeployments_AndCleanupOnlyDeletedConsumer()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await Store.CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId, "shared", "tests", Values(), default));
        var first = await Consumer(group);
        var second = await Consumer(group);
        var binding1 = new SecretBinding(Id(), "pole/web-service", first.ResourceId, true, [], new() { ["password"] = "PASSWORD" });
        var binding2 = new SecretBinding(Id(), "pole/web-service", second.ResourceId, false, [second.Id], new());
        Metadata(await Store.BindAsync(group.TenantId, secret.Id, binding1, default));
        Metadata(await Store.BindAsync(group.TenantId, secret.Id, binding2, default));
        Assert.Equal(2, (await Store.GetAsync(group.TenantId, secret.Id, default))!.Bindings.Length);
        AssertError<SecretConflict>(await Store.DeleteAsync(group.TenantId, secret.Id, default));
        AssertError<SecretConflict>(await Store.RotateAsync(group.TenantId, secret.Id, 1, new() { ["other"] = "x" }, default));
        var third = await Consumer(group);
        AssertError<SecretReferenceRejected>(await Store.BindAsync(group.TenantId, secret.Id,
            binding2 with { Id = Id(), ConsumerId = third.ResourceId }, default));
        var otherGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("other"));
        var foreign = await Consumer(otherGroup);
        AssertError<SecretReferenceRejected>(await Store.BindAsync(group.TenantId, secret.Id,
            binding1 with { Id = Id(), ConsumerId = foreign.ResourceId }, default));
        var otherScope = IsolationScopeFactory.CreateAdditional(group.TenantId, group.Id, "other", IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(otherScope);
        var scoped = await Consumer(group, otherScope.Id);
        AssertError<SecretReferenceRejected>(await Store.BindAsync(group.TenantId, secret.Id,
            binding1 with { Id = Id(), ConsumerId = scoped.ResourceId }, default));
        AssertError<SecretMissing>(await Store.BindAsync(Id(), secret.Id, binding1, default));
        await Store.CleanupConsumerAsync(Id(), group.Id, binding1.ConsumerType, binding1.ConsumerId, default);
        Assert.Equal(2, (await Store.GetAsync(group.TenantId, secret.Id, default))!.Bindings.Length);
        await Store.CleanupConsumerAsync(group.TenantId, group.Id, binding1.ConsumerType, binding1.ConsumerId, default);
        await Store.CleanupConsumerAsync(group.TenantId, group.Id, binding1.ConsumerType, binding1.ConsumerId, default);
        Assert.Equal(binding2.Id, Assert.Single((await Store.GetAsync(group.TenantId, secret.Id, default))!.Bindings).Id);
        Metadata(await Store.DetachAsync(group.TenantId, secret.Id, binding2.Id, default));
        Assert.True(await Store.DeleteAsync(group.TenantId, secret.Id, default) is SecretDeleted);
        Assert.Equal(0, await Versions.CountDocumentsAsync(x => x.SecretId == secret.Id));
    }

    [Fact]
    public async Task RetainedSecretPreventsGroupAndScopeDeletion()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var scope = IsolationScopeFactory.CreateAdditional(group.TenantId, group.Id, "secret-scope", IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(scope);
        Metadata(await Store.CreateAsync(group.TenantId, group.Id, scope.Id, "retained", "tests", Values(), default));
        var scopeResult = await Services.GetRequiredService<BeginIsolationScopeDeletion>()(group.TenantId, group.Id, scope.Id);
        Assert.True(scopeResult is IsolationScopeDeletionError error && error is IsolationScopeReferenced);
        var groupResult = await Services.GetRequiredService<BeginResourceGroupDeletion>()(group.TenantId, group.Id);
        Assert.True(groupResult is ResourceGroupDeletionError groupError && groupError is ResourceGroupContainsResources);
    }

    [Fact]
    public async Task ConcurrentRotationsCommitExactlyOneVersionAndEvent()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await Store.CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId, "race", "tests", Values(), default));
        var results = await Task.WhenAll(Store.RotateAsync(group.TenantId, secret.Id, 1, Values("a"), default),
            Store.RotateAsync(group.TenantId, secret.Id, 1, Values("b"), default));
        Assert.Single(results, x => x is SecretMetadata);
        Assert.Single(results, x => x is SecretError);
        Assert.Equal(2, await Versions.CountDocumentsAsync(x => x.SecretId == secret.Id));
        Assert.Equal(2, await DbContext.CountOutboxMessagesAsync("pole.secrets/v1/secret-changed"));
    }

    [Fact]
    public async Task MutationAndOutboxAreAtomic_WhenOutboxWriteFails()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var failing = new SecretStore(Services.GetRequiredService<IMongoCollection<Secret>>(), Versions,
            Services.GetRequiredService<MongoRunTransaction>(), Services.GetRequiredService<IEnvelopeEncryptionService>(),
            Services.GetRequiredService<AdmitResourceScopeReference>(), Services.GetRequiredService<AdmitResourceDeploymentReferences>(),
            Services.GetRequiredService<IResourceRegistrar>(), Services.GetRequiredService<LoadResourceDeploymentSet>(),
            (_, _, _) => throw new InvalidOperationException("Synthetic outbox failure"), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.CreateAsync(group.TenantId,
            group.Id, group.DefaultIsolationScopeId, "rollback", "tests", Values(), default));
        Assert.Empty((await Store.ListAsync(group.TenantId, null, 50, default)).Items);
        Assert.Equal(0, await Versions.CountDocumentsAsync(Builders<SecretVersion>.Filter.Empty));
        Assert.False(await DbContext.ResourceRegistrations.Find(x => x.ResourceTypeId == "pole/secret").AnyAsync());
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync("pole.secrets/v1/secret-changed"));
    }

    [Fact]
    public async Task RotationVersusBindingCannotRemoveABoundKey()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await Store.CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId, "rotation-bind", "tests", Values(), default));
        var consumer = await Consumer(group);
        var binding = new SecretBinding(Id(), "pole/web-service", consumer.ResourceId, true, [], new() { ["password"] = "PASSWORD" });
        var outcomes = await Task.WhenAll(Store.RotateAsync(group.TenantId, secret.Id, 1, new() { ["other"] = "value" }, default),
            Store.BindAsync(group.TenantId, secret.Id, binding, default));
        Assert.Single(outcomes, x => x is SecretMetadata);
        Assert.Single(outcomes, x => x is SecretError);
        var current = await Store.GetAsync(group.TenantId, secret.Id, default);
        Assert.NotNull(current);
        Assert.True(current.Bindings.Length == 0 || current.KeyNames.Contains("password"));
    }

    [Fact]
    public async Task DeletionVersusBindingCannotLeaveAnOrphanBinding()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await Store.CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId, "delete-bind", "tests", Values(), default));
        var consumer = await Consumer(group);
        var binding = new SecretBinding(Id(), "pole/web-service", consumer.ResourceId, true, [], new());
        var outcomes = await Task.WhenAll(Store.DeleteAsync(group.TenantId, secret.Id, default),
            Store.BindAsync(group.TenantId, secret.Id, binding, default));
        Assert.Single(outcomes, x => x is SecretError);
        var current = await Store.GetAsync(group.TenantId, secret.Id, default);
        Assert.True(current is null || current.Bindings.Length == 1);
        Assert.Equal(current is null ? 0 : 1, await Versions.CountDocumentsAsync(x => x.SecretId == secret.Id));
    }

    [Fact]
    public async Task AllDeploymentsTracksLaterPlacements_WhileExplicitSelectionStaysLimited()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var dynamicSecret = Metadata(await Store.CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId, "dynamic", "tests", Values(), default));
        var explicitSecret = Metadata(await Store.CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId, "explicit", "tests", Values(), default));
        var consumer = await Consumer(group);
        Metadata(await Store.BindAsync(group.TenantId, dynamicSecret.Id,
            new(Id(), "pole/web-service", consumer.ResourceId, true, [], new()), default));
        Metadata(await Store.BindAsync(group.TenantId, explicitSecret.Id,
            new(Id(), "pole/web-service", consumer.ResourceId, false, [consumer.Id], new()), default));
        var later = consumer with { Id = Id(), PlacementKey = Id() };
        await DbContext.ResourceDeployments.InsertOneAsync(later);
        var dynamicUsage = await Store.UsageAsync((await Store.GetAsync(group.TenantId, dynamicSecret.Id, default))!, default);
        var explicitUsage = await Store.UsageAsync((await Store.GetAsync(group.TenantId, explicitSecret.Id, default))!, default);
        Assert.Equal(2, Assert.Single(dynamicUsage).AffectedDeploymentIds.Length);
        Assert.Equal(consumer.Id, Assert.Single(Assert.Single(explicitUsage).AffectedDeploymentIds));
    }

    [Fact]
    public async Task UnregisterPublishesDurableCleanupEvent_AndRejectsNewBindings()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await Store.CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId, "unregister", "tests", Values(), default));
        var consumer = await Consumer(group);
        Metadata(await Store.BindAsync(group.TenantId, secret.Id,
            new(Id(), "pole/web-service", consumer.ResourceId, true, [], new()), default));
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await Services.GetRequiredService<IResourceRegistrar>().UnregisterAsync(group.TenantId, group.Id,
            ResourceTypeId.Create("pole/web-service"), consumer.ResourceId, session);
        await session.CommitTransactionAsync();
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync("pole.resource-groups/v1/resource-unregistered"));
        var cleanup = new RemoveSecretBindingsOnResourceUnregistered(Store);
        await cleanup.Handle(new Pole.Features.ResourceGroups.Events.ResourceUnregisteredEvent(group.Id, "pole/web-service", consumer.ResourceId)
            { EventCore = Pole.Features.Events.Abstractions.EventCore.Create(group.TenantId) });
        Assert.Empty((await Store.GetAsync(group.TenantId, secret.Id, default))!.Bindings);
        AssertError<SecretReferenceRejected>(await Store.BindAsync(group.TenantId, secret.Id,
            new(Id(), "pole/web-service", consumer.ResourceId, true, [], new()), default));
    }

    private async Task<ResourceDeployment> Consumer(ResourceGroup group, string? scopeId = null)
    {
        var resourceId = Id();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var registration = await Services.GetRequiredService<IResourceRegistrar>().RegisterAsync(group.TenantId, group.Id,
            new(ResourceTypeId.Create("pole/web-service"), resourceId, "consumer"), session);
        Assert.True(registration is Ok<RegisteredResource>);
        await session.CommitTransactionAsync();
        var now = DateTime.UtcNow;
        var placement = ResolvedResourcePlacement.Create(Id(), TestData.DefaultDeploymentTargetId, "test-namespace", 1, 1,
            ResolvedIngressConfiguration.Create("traefik", "letsencrypt-prod"), null, ResourceDeploymentTestData.Compute(), now);
        var deployment = ResourceDeploymentFactory.Create(group.TenantId, ResourceTypeId.Create("pole/web-service"), resourceId,
            group.Id, scopeId ?? group.DefaultIsolationScopeId, placement, ResourcePlacementCapability.MultiTarget, true,
            new ResourceDeploymentFactory.CreateOptions(Id(), now));
        await DbContext.ResourceDeployments.InsertOneAsync(deployment);
        return deployment;
    }

    private static SecretMetadata Metadata(SecretMutationOutcome outcome) => outcome switch
    {
        SecretMetadata metadata => metadata,
        _ => throw new Xunit.Sdk.XunitException("Expected Secret metadata.")
    };
    private static void AssertError<T>(SecretMutationOutcome outcome) where T : notnull
    {
        Assert.True(outcome is SecretError);
        var error = outcome switch { SecretError value => value, _ => throw new InvalidOperationException() };
        Assert.IsType<T>(error.Value);
    }
}
