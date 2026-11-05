using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Application;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Domain;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Secrets.Application.Endpoints;
using Pole.Features.Secrets.Migrations;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Events;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

public sealed class SecretIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Binding_submodels_round_trip_current_mongo_documents_without_migration()
    {
        var secret = SecretFactory.Create(Id(), TestData.TenantId, Id(), Id(), "current-bindings", "test",
                ["password", "user"], DateTime.UtcNow)
            .Bind(SecretBinding.Create(Id(), "pole/web-service", Id(), new AllSecretDeployments(), [])
                .RequireKeysThroughRollout(["password"]).RequireKeysThroughRollout(["user"]), DateTime.UtcNow);
        await Fixture.Database.GetCollection<Secret>("Secrets").InsertOneAsync(secret);

        var restored = await Services.GetRequiredService<LoadSecretById>()(secret.TenantId, secret.Id, default);
        var binding = Assert.Single(Assert.IsType<Secret>(restored).Bindings);
        Assert.Equal(secret.Bindings.Single().Consumer, binding.Consumer);
        Assert.Equal(["user"], binding.Pins.Required.ToArray());
        Assert.Equal(["password"], binding.Pins.Retained.ToArray());
        Assert.Throws<InvalidOperationException>(() => restored!.Rotate(["user"], DateTime.UtcNow));
    }

    [Theory]
    [InlineData("key", ConsumerSecretReferenceError.SecretKeyUnavailable)]
    [InlineData("coverage", ConsumerSecretReferenceError.SecretDeploymentCoverageUnavailable)]
    [InlineData("scope", ConsumerSecretReferenceError.SecretBindingUnavailable)]
    [InlineData("tenant", ConsumerSecretReferenceError.SecretBindingUnavailable)]
    public async Task Consumer_references_reject_unauthorized_keys_incomplete_coverage_foreign_scope_or_tenant(
        string scenario, ConsumerSecretReferenceError reason)
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId, "authority-refs", "tests", new() { ["password"] = "synthetic", ["other"] = "synthetic" }, default));
        var consumer = await Consumer(group);
        var binding = SecretBinding.Create(Id(), "pole/web-service", consumer.ResourceId, new SelectedSecretDeployments([consumer.Id]), new Dictionary<string, string> { ["password"] = "APP_PASSWORD" });
        Metadata(await BindAsync(group.TenantId, secret.Id, binding, default));
        if (scenario == "coverage") await DbContext.ResourceDeployments.InsertOneAsync(consumer with { Id = Id() });
        using var session = await Fixture.Client.StartSessionAsync(); session.StartTransaction();
        var result = await Services.GetRequiredService<AdmitConsumerSecretReferences>()(
            new AdmitConsumerSecretReferencesRequest(
                TenantId: scenario == "tenant" ? Id() : group.TenantId,
                ResourceGroupId: group.Id,
                IsolationScopeId: scenario == "scope" ? Id() : group.DefaultIsolationScopeId,
                ConsumerType: ResourceTypeId.Create("pole/web-service"),
                ConsumerId: consumer.ResourceId,
                PreviousReferences: [],
                DesiredReferences: [new(secret.Id, scenario == "key" ? "other" : "password")],
                Availability: ConsumerSecretAvailabilityPolicy.RequireAvailable,
                Retention: PreviousSecretReferenceRetention.DoNotRetain),
            session: session,
            ct: default);
        Assert.Equal(reason, Assert.IsType<ConsumerSecretReferenceUnavailable>(result.Value).Reason);
        await session.AbortTransactionAsync();
        Assert.Empty((await Services.GetRequiredService<LoadSecretById>()(group.TenantId, secret.Id,
            default))!.Bindings.Single(item => item.Id == binding.Id).Pins.Required);
    }

    [Fact]
    public async Task Consumer_references_pin_keys_without_changing_version_and_removal_releases_only_that_consumers_pins()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId, "logical-refs", "tests", Values(), default));
        var first = await Consumer(group);
        var second = await Consumer(group);
        var binding = SecretBinding.Create(Id(), "pole/web-service", first.ResourceId, new AllSecretDeployments(), []);
        var otherBinding = SecretBinding.Create(Id(), "pole/web-service", second.ResourceId, new AllSecretDeployments(), []);
        Metadata(await BindAsync(group.TenantId, secret.Id, binding, default));
        Metadata(await BindAsync(group.TenantId, secret.Id, otherBinding, default));
        var before = (await Services.GetRequiredService<LoadSecretById>()(group.TenantId, secret.Id, default))!;
        var admit = Services.GetRequiredService<AdmitConsumerSecretReferences>();
        using var session = await Fixture.Client.StartSessionAsync(); session.StartTransaction();
        var admitted = await admit(
            new AdmitConsumerSecretReferencesRequest(
                TenantId: group.TenantId,
                ResourceGroupId: group.Id,
                IsolationScopeId: group.DefaultIsolationScopeId,
                ConsumerType: ResourceTypeId.Create("pole/web-service"),
                ConsumerId: first.ResourceId,
                PreviousReferences: [],
                DesiredReferences: [new(secret.Id, "password")],
                Availability: ConsumerSecretAvailabilityPolicy.RequireAvailable,
                Retention: PreviousSecretReferenceRetention.DoNotRetain),
            session: session,
            ct: default);
        Assert.IsType<ConsumerSecretReferencesAdmitted>(admitted.Value); await session.CommitTransactionAsync();
        var pinned = (await Services.GetRequiredService<LoadSecretById>()(group.TenantId, secret.Id, default))!;
        Assert.Equal(before.Revision, pinned.Revision); Assert.Equal(before.CurrentVersion, pinned.CurrentVersion);
        Assert.Equal(new[] { "password" },
            pinned.Bindings.Single(item => item.Id == binding.Id).Pins.Required.ToArray());
        Assert.Empty(pinned.Bindings.Single(item => item.Id == otherBinding.Id).Pins.Required);
        AssertError<SecretMappedKeysRequired>(await RotateAsync(group.TenantId, secret.Id, 1, new() { ["other"] = "new-value" }, default));
        session.StartTransaction();
        var removed = await admit(
            new AdmitConsumerSecretReferencesRequest(
                TenantId: group.TenantId,
                ResourceGroupId: group.Id,
                IsolationScopeId: group.DefaultIsolationScopeId,
                ConsumerType: ResourceTypeId.Create("pole/web-service"),
                ConsumerId: first.ResourceId,
                PreviousReferences: [new(secret.Id, "password")],
                DesiredReferences: [],
                Availability: ConsumerSecretAvailabilityPolicy.RequireAvailable,
                Retention: PreviousSecretReferenceRetention.DoNotRetain),
            session: session,
            ct: default);
        Assert.IsType<ConsumerSecretReferencesAdmitted>(removed.Value); await session.CommitTransactionAsync();
        Metadata(await RotateAsync(group.TenantId, secret.Id, 1, new() { ["other"] = "new-value" }, default));
    }

    [Theory]
    [InlineData(PreviousSecretReferenceRetention.DoNotRetain, false)]
    [InlineData(PreviousSecretReferenceRetention.KeepUntilRolloutConfirmed, true)]
    public async Task Reference_retention_policy_controls_old_keys_without_releasing_existing_rollout_pins(
        PreviousSecretReferenceRetention retention,
        bool retainsOldKey)
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId, "retention",
            "tests",
            new() { ["password"] = "synthetic", ["other"] = "synthetic" }, default));
        var consumer = await Consumer(group);
        var binding = SecretBinding.Create(Id(), "pole/web-service", consumer.ResourceId, new AllSecretDeployments(),
            []);
        Metadata(await BindAsync(group.TenantId, secret.Id, binding, default));
        var admit = Services.GetRequiredService<AdmitConsumerSecretReferences>();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<ConsumerSecretReferencesAdmitted>((await admit(
            new AdmitConsumerSecretReferencesRequest(
                TenantId: group.TenantId,
                ResourceGroupId: group.Id,
                IsolationScopeId: group.DefaultIsolationScopeId,
                ConsumerType: ResourceTypeId.Create("pole/web-service"),
                ConsumerId: consumer.ResourceId,
                PreviousReferences: [],
                DesiredReferences: [new(secret.Id, "password")],
                Availability: ConsumerSecretAvailabilityPolicy.RequireAvailable,
                Retention: retention),
            session: session,
            ct: default)).Value);
        await session.CommitTransactionAsync();
        session.StartTransaction();
        Assert.IsType<ConsumerSecretReferencesAdmitted>((await admit(
            new AdmitConsumerSecretReferencesRequest(
                TenantId: group.TenantId,
                ResourceGroupId: group.Id,
                IsolationScopeId: group.DefaultIsolationScopeId,
                ConsumerType: ResourceTypeId.Create("pole/web-service"),
                ConsumerId: consumer.ResourceId,
                PreviousReferences: [new(secret.Id, "password")],
                DesiredReferences: [new(secret.Id, "other")],
                Availability: ConsumerSecretAvailabilityPolicy.RequireAvailable,
                Retention: retention),
            session: session,
            ct: default)).Value);
        await session.CommitTransactionAsync();

        var current = await Services.GetRequiredService<LoadSecretById>()(group.TenantId, secret.Id, default);
        var pins = Assert.Single(current!.Bindings).Pins;
        Assert.Equal(["other"], pins.Required.ToArray());
        Assert.Equal(retainsOldKey ? ["password"] : Array.Empty<string>(), pins.Retained.ToArray());
    }

    [Theory]
    [InlineData(ConsumerSecretAvailabilityPolicy.RequireAvailable, false)]
    [InlineData(ConsumerSecretAvailabilityPolicy.AllowUnavailableForCleanup, true)]
    public async Task Availability_policy_allows_independent_cleanup_of_missing_secrets(
        ConsumerSecretAvailabilityPolicy availability,
        bool admitted)
    {
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var result = await Services.GetRequiredService<AdmitConsumerSecretReferences>()(
            new AdmitConsumerSecretReferencesRequest(
                TenantId: TestData.TenantId,
                ResourceGroupId: Id(),
                IsolationScopeId: Id(),
                ConsumerType: ResourceTypeId.Create("pole/web-service"),
                ConsumerId: Id(),
                PreviousReferences: [],
                DesiredReferences: [new(Id(), "key")],
                Availability: availability,
                Retention: PreviousSecretReferenceRetention.DoNotRetain),
            session: session,
            ct: default);
        Assert.Equal(admitted, result is ConsumerSecretReferencesAdmitted);
        await session.AbortTransactionAsync();
    }

    private IMongoCollection<SecretVersion> Versions => Fixture.Database.GetCollection<SecretVersion>("SecretVersions");

    private static string Id() => ObjectId.GenerateNewId().ToString();

    private static Dictionary<string, string> Values(string value = "synthetic-password") => new()
    {
        ["password"] = value
    };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new SecretIndexesMigration().Up(Fixture.Database);
    }

    [Fact]
    public async Task IndexesUseSerializedIdentityAndTenantScope()
    {
        using var cursor = await Fixture.Database.GetCollection<BsonDocument>("Secrets").Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        Assert.Contains(indexes, index => index["key"].AsBsonDocument.Equals(
            new BsonDocument { { "TenantId", 1 }, { "_id", 1 } }));
        Assert.Contains(indexes, index => index["key"].AsBsonDocument.Equals(new BsonDocument
        {
            { "TenantId", 1 },
            { "Bindings.Consumer.ResourceType", 1 },
            { "Bindings.Consumer.ResourceId", 1 }
        }));
        Assert.Contains(indexes, index => index.GetValue("unique", false).AsBoolean &&
            index["key"].AsBsonDocument.Equals(new BsonDocument
            {
                { "TenantId", 1 },
                { "NormalizedName", 1 }
            }));
    }

    [Fact]
    public async Task NamesAreUniqueAcrossGroupsAndScopesButIndependentAcrossTenantsAndResourceTypes()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("first"));
        var otherGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("second"));
        var foreignGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("foreign", Id()));
        var scope = IsolationScopeFactory.CreateAdditional(
            group.TenantId, group.Id, "additional", IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(scope);
        Metadata(await CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId,
            "payment-provider", "tests", Values(), default));

        AssertError<SecretNameAlreadyExists>(await CreateAsync(
            group.TenantId, otherGroup.Id, otherGroup.DefaultIsolationScopeId,
            " PAYMENT-PROVIDER ", "tests", Values(), default));
        AssertError<SecretNameAlreadyExists>(await CreateAsync(
            group.TenantId, group.Id, scope.Id, "payment-provider", "tests", Values(), default));
        Metadata(await CreateAsync(foreignGroup.TenantId, foreignGroup.Id, foreignGroup.DefaultIsolationScopeId,
            "payment-provider", "tests", Values(), default));

        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var registration = await Services.GetRequiredService<IResourceRegistrar>().RegisterAsync(
            new RegisterResourceRequest(
                TenantId: group.TenantId,
                ResourceGroupId: group.Id,
                Resource: new(ResourceTypeId.Create("pole/web-service"), Id(), "payment-provider")),
            session: session);
        Assert.IsType<RegisteredResource>(registration.Value);
        await session.CommitTransactionAsync();
        Assert.Equal(2, await Versions.CountDocumentsAsync(Builders<SecretVersion>.Filter.Empty));
        Assert.Equal(2, await DbContext.CountOutboxMessagesAsync("pole.secrets/v1/secret-changed"));
    }

    [Fact]
    public async Task ConcurrentCreatesInDifferentGroupsReserveExactlyOneTenantName()
    {
        var first = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("first"));
        var second = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("second"));
        var outcomes = await Task.WhenAll(
            CreateAsync(first.TenantId, first.Id, first.DefaultIsolationScopeId,
                "shared-name", "tests", Values(), default),
            CreateAsync(second.TenantId, second.Id, second.DefaultIsolationScopeId,
                " SHARED-NAME ", "tests", Values(), default));
        Assert.Single(outcomes, outcome => outcome is Secret);
        Assert.Single(outcomes, outcome => outcome is SecretNameAlreadyExists);
        Assert.Equal(1, await Versions.CountDocumentsAsync(Builders<SecretVersion>.Filter.Empty));
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync("pole.secrets/v1/secret-changed"));
    }

    [Fact]
    public async Task DeletedNameCanBeReusedWithANewIdentityAndNoOldVersions()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var first = Metadata(await CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId,
            "reusable", "tests", Values(), default));
        await DeleteAsync(group.TenantId, first.Id, default);
        var replacement = Metadata(await CreateAsync(group.TenantId, group.Id, group.DefaultIsolationScopeId,
            "reusable", "tests", Values(), default));
        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Equal(1, replacement.CurrentVersion);
        Assert.Null(await Services.GetRequiredService<LoadSecretById>()(group.TenantId, first.Id, default));
        Assert.Equal(0, await Versions.CountDocumentsAsync(version => version.SecretId == first.Id));
    }

    [Fact]
    public async Task CreateRotate_ReadsOnlyMetadata_StoresEncryptedImmutableVersions_AndFencesTenants()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = Metadata(await CreateAsync(
            group.TenantId,
            group.Id,
            group.DefaultIsolationScopeId,
            "credentials",
            "tests",
            Values(),
            default));
        var first = await Versions.Find(x => x.SecretId == created.Id).SingleAsync();
        Assert.DoesNotContain("synthetic-password", first.ToJson());
        Assert.DoesNotContain("Ciphertext", JsonSerializer.Serialize(created));
        var decrypted = await Services.GetRequiredService<IEnvelopeEncryptionService>().DecryptAsync(
            first.Value,
            EnvelopeEncryptionContext.ForTenant(group.TenantId, "Secret", created.Id, "values", 1));
        Assert.IsType<byte[]>(decrypted.Value);
        Assert.Null(await Services.GetRequiredService<LoadSecretById>()(Id(), created.Id, default));
        AssertError<SecretMissing>(await RotateAsync(Id(), created.Id, 1, Values(), default));
        var rotated = Metadata(await RotateAsync(
            group.TenantId,
            created.Id,
            1,
            Values("rotated-password"),
            default));
        Assert.Equal(2, rotated.CurrentVersion);
        Assert.Equal(2, await Versions.CountDocumentsAsync(x => x.SecretId == created.Id));
        Assert.Equal(first.ToJson(), (await Versions.Find(x => x.Id == first.Id).SingleAsync()).ToJson());
        AssertError<SecretVersionConflict>(await RotateAsync(group.TenantId, created.Id, 1, Values(), default));
        AssertError<SecretNameAlreadyExists>(await CreateAsync(
            group.TenantId,
            group.Id,
            group.DefaultIsolationScopeId,
            " CREDENTIALS ",
            "tests",
            Values(),
            default));
        var outbox = await DbContext.OutboxMessages.Find(Builders<OutboxMessage>.Filter.Empty).ToListAsync();
        Assert.DoesNotContain("synthetic-password", outbox.ToJson());
        Assert.DoesNotContain("rotated-password", outbox.ToJson());
        Assert.DoesNotContain("Ciphertext", outbox.ToJson());
        var events = outbox.Where(x => x.EventType == "pole.secrets/v1/secret-changed")
            .Select(x => JsonSerializer.Deserialize<SecretChangedEvent>(
                x.PayloadJson, EventExtensions.SerializerOptions)!)
            .OrderBy(x => x.Version)
            .ToArray();
        Assert.Equal(2, events.Length);
        Assert.IsType<SecretCreated>(events[0].Change);
        Assert.IsType<SecretRotated>(events[1].Change);
        Assert.All(events, evt => Assert.Equal(group.TenantId, evt.EventCore.TenantId));
    }

    [Fact]
    public async Task BindingsShareOnlyWithinScope_RejectForeignDeployments_AndCleanupOnlyDeletedConsumer()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await CreateAsync(
            group.TenantId,
            group.Id,
            group.DefaultIsolationScopeId,
            "shared",
            "tests",
            Values(),
            default));
        var first = await Consumer(group);
        var second = await Consumer(group);
        var binding1 = SecretBinding.Create(
            Id(),
            "pole/web-service",
            first.ResourceId,
            new AllSecretDeployments(),
            new Dictionary<string, string>()
            {
                ["password"] = "PASSWORD"
            });
        var binding2 = SecretBinding.Create(
            Id(),
            "pole/web-service",
            second.ResourceId,
            new SelectedSecretDeployments([second.Id]),
            new Dictionary<string, string>());
        Metadata(await BindAsync(group.TenantId, secret.Id, binding1, default));
        Metadata(await BindAsync(group.TenantId, secret.Id, binding2, default));
        Assert.Equal(2, (await Services.GetRequiredService<LoadSecretById>()(group.TenantId, secret.Id, default))!.Bindings.Length);
        AssertError<SecretBindingsRemain>(await DeleteAsync(group.TenantId, secret.Id, default));
        AssertError<SecretMappedKeysRequired>(await RotateAsync(group.TenantId, secret.Id, 1, new()
        {
            ["other"] = "x"
        }, default));
        var third = await Consumer(group);
        AssertError<SecretReferenceRejected>(await BindAsync(
            group.TenantId,
            secret.Id,
            binding2 with
            {
                Id = Id(),
                Consumer = binding2.Consumer with { ResourceId = third.ResourceId }
            },
            default));
        var otherGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("other"));
        var foreign = await Consumer(otherGroup);
        AssertError<SecretReferenceRejected>(await BindAsync(
            group.TenantId,
            secret.Id,
            binding1 with
            {
                Id = Id(),
                Consumer = binding1.Consumer with { ResourceId = foreign.ResourceId }
            },
            default));
        var otherScope = IsolationScopeFactory.CreateAdditional(
            group.TenantId,
            group.Id,
            "other",
            IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(otherScope);
        var scoped = await Consumer(group, otherScope.Id);
        AssertError<SecretReferenceRejected>(await BindAsync(
            group.TenantId,
            secret.Id,
            binding1 with
            {
                Id = Id(),
                Consumer = binding1.Consumer with { ResourceId = scoped.ResourceId }
            },
            default));
        AssertError<SecretMissing>(await BindAsync(Id(), secret.Id, binding1, default));
        await Services.GetRequiredService<RemoveConsumerSecretBindings>()(Id(), group.Id,
            binding1.Consumer.ResourceType, binding1.Consumer.ResourceId, default);
        Assert.Equal(2, (await Services.GetRequiredService<LoadSecretById>()(group.TenantId, secret.Id, default))!.Bindings.Length);
        await Services.GetRequiredService<RemoveConsumerSecretBindings>()(
            group.TenantId,
            group.Id,
            binding1.Consumer.ResourceType,
            binding1.Consumer.ResourceId,
            default);
        await Services.GetRequiredService<RemoveConsumerSecretBindings>()(
            group.TenantId,
            group.Id,
            binding1.Consumer.ResourceType,
            binding1.Consumer.ResourceId,
            default);
        Assert.Equal(
            binding2.Id,
            Assert.Single((await Services.GetRequiredService<LoadSecretById>()(group.TenantId, secret.Id, default))!.Bindings).Id);
        Metadata(await DetachAsync(group.TenantId, secret.Id, binding2.Id, default));
        Assert.True(await DeleteAsync(group.TenantId, secret.Id, default) is Unit);
        Assert.Equal(0, await Versions.CountDocumentsAsync(x => x.SecretId == secret.Id));
    }

    [Fact]
    public async Task RetainedSecretPreventsGroupAndScopeDeletion()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var scope = IsolationScopeFactory.CreateAdditional(
            group.TenantId,
            group.Id,
            "secret-scope",
            IsolationScopeFactory.DefaultOptions);
        await DbContext.IsolationScopes.InsertOneAsync(scope);
        Metadata(await CreateAsync(
            group.TenantId,
            group.Id,
            scope.Id,
            "retained",
            "tests",
            Values(),
            default));
        var scopeResult = await Services.GetRequiredService<BeginIsolationScopeDeletion>()(group.TenantId, group.Id, scope.Id);
        Assert.True(scopeResult is IsolationScopeDeletionError error && error is IsolationScopeReferenced);
        var groupResult = await Services.GetRequiredService<BeginResourceGroupDeletion>()(group.TenantId, group.Id);
        Assert.True(groupResult is ResourceGroupDeletionError groupError && groupError is ResourceGroupContainsResources);
    }

    [Fact]
    public async Task ConcurrentRotationsCommitExactlyOneVersionAndEvent()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await CreateAsync(
            group.TenantId,
            group.Id,
            group.DefaultIsolationScopeId,
            "race",
            "tests",
            Values(),
            default));
        var results = await Task.WhenAll(
            RotateAsync(group.TenantId, secret.Id, 1, Values("a"), default),
            RotateAsync(group.TenantId, secret.Id, 1, Values("b"), default));
        Assert.Single(results, x => x is Secret);
        Assert.Single(results, x => x is SecretVersionConflict);
        Assert.Equal(2, await Versions.CountDocumentsAsync(x => x.SecretId == secret.Id));
        Assert.Equal(2, await DbContext.CountOutboxMessagesAsync("pole.secrets/v1/secret-changed"));
    }

    [Fact]
    public async Task MutationAndOutboxAreAtomic_WhenOutboxWriteFails()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var registrations = new ServiceCollection();
        registrations.AddSingleton(Fixture.Database);
        registrations.AddSingleton(Services.GetRequiredService<MongoRunTransaction>());
        registrations.AddSingleton(Services.GetRequiredService<AdmitResourceScopeReference>());
        registrations.AddSingleton(Services.GetRequiredService<IResourceRegistrar>());
        registrations.AddSingleton(Services.GetRequiredService<IEnvelopeEncryptionService>());
        registrations.AddSingleton(TimeProvider.System);
        registrations.AddSecretsFeature();
        registrations.AddSingleton<StoreOutboxWrites>((_, _, _) =>
            throw new InvalidOperationException("Synthetic outbox failure"));
        using var provider = registrations.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var failing = scope.ServiceProvider.GetRequiredService<PersistCreatedSecret>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing(
            group.TenantId,
            group.Id,
            group.DefaultIsolationScopeId,
            "rollback",
            "tests",
            Values(),
            default));
        Assert.Empty((await Services.GetRequiredService<LoadSecretPage>()(group.TenantId, null, 50, default)).Items);
        Assert.Equal(0, await Versions.CountDocumentsAsync(Builders<SecretVersion>.Filter.Empty));
        Assert.False(await DbContext.ResourceRegistrations
            .Find(x => x.ResourceTypeId == "pole/secret")
            .AnyAsync());
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync("pole.secrets/v1/secret-changed"));
    }

    [Fact]
    public async Task RotationVersusBindingCannotRemoveABoundKey()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await CreateAsync(
            group.TenantId,
            group.Id,
            group.DefaultIsolationScopeId,
            "rotation-bind",
            "tests",
            Values(),
            default));
        var consumer = await Consumer(group);
        var binding = SecretBinding.Create(
            Id(),
            "pole/web-service",
            consumer.ResourceId,
            new AllSecretDeployments(),
            new Dictionary<string, string>()
            {
                ["password"] = "PASSWORD"
            });
        var outcomes = await Task.WhenAll(
            RotateAsync(group.TenantId, secret.Id, 1, new()
            {
                ["other"] = "value"
            }, default),
            BindAsync(group.TenantId, secret.Id, binding, default));
        Assert.Single(outcomes, x => x is Secret);
        Assert.Single(outcomes, x => x is SecretMappedKeysRequired or SecretUnknownKeys);
        var current = await Services.GetRequiredService<LoadSecretById>()(group.TenantId, secret.Id, default);
        Assert.NotNull(current);
        Assert.True(current.Bindings.Length == 0 || current.KeyNames.Contains("password"));
    }

    [Fact]
    public async Task DeletionVersusBindingCannotLeaveAnOrphanBinding()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await CreateAsync(
            group.TenantId,
            group.Id,
            group.DefaultIsolationScopeId,
            "delete-bind",
            "tests",
            Values(),
            default));
        var consumer = await Consumer(group);
        var binding = SecretBinding.Create(
            Id(),
            "pole/web-service",
            consumer.ResourceId,
            new AllSecretDeployments(),
            new Dictionary<string, string>());
        var outcomes = await Task.WhenAll(
            DeleteAsync(group.TenantId, secret.Id, default),
            BindAsync(group.TenantId, secret.Id, binding, default));
        Assert.Single(outcomes, x => x is SecretBindingsRemain or SecretMissing);
        var current = await Services.GetRequiredService<LoadSecretById>()(group.TenantId, secret.Id, default);
        Assert.True(current is null || current.Bindings.Length == 1);
        Assert.Equal(current is null ? 0 : 1, await Versions.CountDocumentsAsync(x => x.SecretId == secret.Id));
    }

    [Fact]
    public async Task AllDeploymentsTracksLaterPlacements_WhileExplicitSelectionStaysLimited()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var dynamicSecret = Metadata(await CreateAsync(
            group.TenantId,
            group.Id,
            group.DefaultIsolationScopeId,
            "dynamic",
            "tests",
            Values(),
            default));
        var explicitSecret = Metadata(await CreateAsync(
            group.TenantId,
            group.Id,
            group.DefaultIsolationScopeId,
            "explicit",
            "tests",
            Values(),
            default));
        var consumer = await Consumer(group);
        Metadata(await BindAsync(
            group.TenantId,
            dynamicSecret.Id,
            SecretBinding.Create(
                Id(),
                "pole/web-service",
                consumer.ResourceId,
                new AllSecretDeployments(),
                new Dictionary<string, string>()),
            default));
        Metadata(await BindAsync(
            group.TenantId,
            explicitSecret.Id,
            SecretBinding.Create(
                Id(),
                "pole/web-service",
                consumer.ResourceId,
                new SelectedSecretDeployments([consumer.Id]),
                new Dictionary<string, string>()),
            default));
        var later = consumer with
        {
            Id = Id(),
            PlacementKey = Id()
        };
        await DbContext.ResourceDeployments.InsertOneAsync(later);
        var dynamicUsage = await UsageAsync(
            (await Services.GetRequiredService<LoadSecretById>()(group.TenantId, dynamicSecret.Id, default))!,
            default);
        var explicitUsage = await UsageAsync(
            (await Services.GetRequiredService<LoadSecretById>()(group.TenantId, explicitSecret.Id, default))!,
            default);
        Assert.Equal(2, Assert.Single(dynamicUsage).AffectedDeploymentIds.Length);
        Assert.Equal(consumer.Id, Assert.Single(Assert.Single(explicitUsage).AffectedDeploymentIds));
    }

    [Fact]
    public async Task UnregisterPublishesDurableCleanupEvent_AndRejectsNewBindings()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secret = Metadata(await CreateAsync(
            group.TenantId,
            group.Id,
            group.DefaultIsolationScopeId,
            "unregister",
            "tests",
            Values(),
            default));
        var consumer = await Consumer(group);
        Metadata(await BindAsync(
            group.TenantId,
            secret.Id,
            SecretBinding.Create(
                Id(),
                "pole/web-service",
                consumer.ResourceId,
                new AllSecretDeployments(),
                new Dictionary<string, string>()),
            default));
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await Services.GetRequiredService<IResourceRegistrar>().UnregisterAsync(
            new UnregisterResourceRequest(
                TenantId: group.TenantId,
                ResourceGroupId: group.Id,
                ResourceTypeId: ResourceTypeId.Create("pole/web-service"),
                ResourceId: consumer.ResourceId),
            session: session);
        await session.CommitTransactionAsync();
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync("pole.resource-groups/v1/resource-unregistered"));
        var cleanup = new RemoveSecretBindingsOnResourceUnregistered(Services.GetRequiredService<RemoveConsumerSecretBindings>());
        await cleanup.Handle(new ResourceUnregisteredEvent(group.Id, "pole/web-service", consumer.ResourceId)
        {
            EventCore = EventCore.Create(group.TenantId)
        });
        Assert.Empty((await Services.GetRequiredService<LoadSecretById>()(group.TenantId, secret.Id, default))!.Bindings);
        AssertError<SecretReferenceRejected>(await BindAsync(
            group.TenantId,
            secret.Id,
            SecretBinding.Create(
                Id(),
                "pole/web-service",
                consumer.ResourceId,
                new AllSecretDeployments(),
                new Dictionary<string, string>()),
            default));
    }

    private async Task<ResourceDeployment> Consumer(
        ResourceGroup group,
        string? scopeId = null)
    {
        var resourceId = Id();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var registration = await Services.GetRequiredService<IResourceRegistrar>().RegisterAsync(
            new RegisterResourceRequest(
                TenantId: group.TenantId,
                ResourceGroupId: group.Id,
                Resource: new(ResourceTypeId.Create("pole/web-service"), resourceId, "consumer")),
            session: session);
        Assert.IsType<RegisteredResource>(registration.Value);
        await session.CommitTransactionAsync();
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
            ResourceTypeId.Create("pole/web-service"),
            resourceId,
            group.Id,
            scopeId ?? group.DefaultIsolationScopeId,
            placement,
            ResourcePlacementCapability.MultiTarget,
            true,
            ResourceDeploymentFactory.DefaultOptions with
            {
                Id = Id(),
                CreatedAtUtc = now
            });
        await DbContext.ResourceDeployments.InsertOneAsync(deployment);
        return deployment;
    }

    private static SecretMetadata Metadata(object? outcome) =>
        SecretMetadata.From(Assert.IsType<Secret>(outcome));

    private static void AssertError<T>(object? outcome) where T : notnull =>
        Assert.IsType<T>(outcome);

    // Assertions unwrap each operation's distinct result, without inventing a shared production outcome.
    private async Task<object?> CreateAsync(
        string tenantId,
        string groupId,
        string scopeId,
        string name,
        string purpose,
        Dictionary<string, string> values,
        CancellationToken ct) =>
        (await Services.GetRequiredService<PersistCreatedSecret>()(
            tenantId,
            groupId,
            scopeId,
            name,
            purpose,
            values,
            ct)).Value;

    private async Task<object?> RotateAsync(
        string tenantId,
        string id,
        long expectedVersion,
        Dictionary<string, string> values,
        CancellationToken ct) =>
        (await Services.GetRequiredService<PersistRotatedSecret>()(
            tenantId, id, expectedVersion, values, ct)).Value;

    private async Task<object?> BindAsync(
        string tenantId,
        string id,
        SecretBinding binding,
        CancellationToken ct) =>
        (await Services.GetRequiredService<PersistSecretBinding>()(tenantId, id, binding, ct)).Value;

    private async Task<object?> DetachAsync(
        string tenantId,
        string id,
        string bindingId,
        CancellationToken ct) =>
        (await Services.GetRequiredService<RemoveSecretBinding>()(tenantId, id, bindingId, ct)).Value;

    private async Task<object?> DeleteAsync(
        string tenantId,
        string id,
        CancellationToken ct) =>
        (await Services.GetRequiredService<DeleteStoredSecret>()(tenantId, id, ct)).Value;

    private async Task<SecretUsage[]> UsageAsync(
        Secret secret,
        CancellationToken ct)
    {
        var handler = new GetSecretUsageHandler(
            Services.GetRequiredService<LoadSecretById>(),
            Services.GetRequiredService<LoadResourceDeploymentSet>(),
            Services.GetRequiredService<IGitOpsMutations>());
        return Assert.IsType<SecretUsage[]>((await handler.HandleAsync(
            new GetSecretUsageRequest(secret.Id, secret.TenantId), ct)).Value);
    }
}
