using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Events.Abstractions;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Contracts;
using Pole.Workloads;
using Pole.Features.Secrets.Application.Endpoints;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Xunit;

namespace Pole.Features.Secrets.Tests;

public sealed class SecretTests
{
    private static Secret NewSecret() => SecretFactory.Create(
        "secret",
        "tenant",
        "group",
        "scope",
        "Name",
        "purpose",
        ["password", "user"],
        DateTime.UtcNow);

    private static SecretBinding Binding(
        string id,
        string consumer) => SecretBinding.Create(
            id,
            "pole/web-service",
            consumer,
            new AllSecretDeployments(),
            new Dictionary<string, string>
            {
                ["password"] = "PASSWORD"
            });

    [Fact]
    public void Binding_usage_is_explicit_persisted_and_cannot_change_under_an_existing_identity()
    {
        var binding = Binding("binding", "consumer");
        Assert.Equal(Pole.Features.Secrets.Primitives.SecretBindingUsage.Runtime, binding.Usage);
        var bootstrap = binding with { Usage = Pole.Features.Secrets.Primitives.SecretBindingUsage.Bootstrap };
        var roundTrip = BsonSerializer.Deserialize<SecretBinding>(bootstrap.ToBson());
        Assert.Equal(bootstrap.Usage, roundTrip.Usage);
        var secret = NewSecret().Bind(binding, DateTime.UtcNow);
        Assert.IsType<SecretBindingUsageChanged>(secret.DeclareBinding(bootstrap, DateTime.UtcNow).Value);
    }

    [Fact]
    public async Task One_recovery_pass_can_release_independent_consumers_without_reusing_the_old_reference_fence()
    {
        SecretBinding Retained(string id) => SecretBinding.Create(id, "pole/web-service", id,
            new AllSecretDeployments(), [])
            .RequireKeysThroughRollout(["password"]).RequireKeysThroughRollout(["user"]);
        var current = NewSecret().Bind(Retained("first"), DateTime.UtcNow).Bind(Retained("second"), DateTime.UtcNow);
        var originalFence = current.ReferenceFence;
        var reconciler = new SecretKeyRetirementReconciler((_, _, _) => Task.FromResult<Secret?>(current),
            (_, _, revision, fence, binding, _, _) =>
            {
                if (revision != current.Revision || fence != current.ReferenceFence) return Task.FromResult(false);
                current = current.ReleaseRetainedKeys(binding);
                return Task.FromResult(true);
            }, [new ConfirmedRolloutObserver()]);
        await reconciler.ReconcileAsync(current.TenantId, current.Id);
        Assert.All(current.Bindings, binding => Assert.Empty(binding.Pins.Retained));
        Assert.Equal(originalFence + 2, current.ReferenceFence);
    }

    private sealed class ConfirmedRolloutObserver : ISecretConsumerRolloutObserver
    {
        public ResourceTypeId ResourceType => ResourceTypeId.Create("pole/web-service");
        public Task<SecretConsumerRolloutOutcome> ObserveAsync(string tenantId, string consumerId,
            CancellationToken ct) =>
            Task.FromResult<SecretConsumerRolloutOutcome>(new SecretConsumerRolloutConfirmed([new(
                "placement-" + consumerId, 1, 1)]));
    }

    [Fact]
    public void Declaration_metadata_preserves_values_and_rejects_keys_protected_by_old_rollouts()
    {
        var binding = SecretBinding.Create("binding", "pole/web-service", "consumer", new AllSecretDeployments(), [])
            .RequireKeysThroughRollout(["password"]).RequireKeysThroughRollout(["user"]);
        var secret = NewSecret().Bind(binding, DateTime.UtcNow);
        Assert.IsType<SecretDeclaredKeysInUse>(secret.UpdateDeclaredMetadata("changed", ["user"],
            DateTime.UtcNow).Value);
        Assert.Throws<InvalidOperationException>(() => secret.Rotate(["user"], DateTime.UtcNow));
        var released = secret.ReleaseRetainedKeys(binding.Id);
        var updated = Assert.IsType<Secret>(released.UpdateDeclaredMetadata("changed", ["user"],
            DateTime.UtcNow).Value);
        Assert.Equal(secret.CurrentVersion, updated.CurrentVersion);
        Assert.Equal(secret.Revision + 1, updated.Revision);
        Assert.Equal(secret.ReferenceFence + 1, updated.ReferenceFence);
        Assert.Same(updated, updated.UpdateDeclaredMetadata("changed", ["user"], DateTime.UtcNow).Value);
    }

    [Fact]
    public void Declarative_binding_updates_preserve_required_and_retained_aliases_and_return_typed_conflicts()
    {
        var binding = (Binding("a", "one") with
        {
            KeyMappings = Binding("a", "one").KeyMappings.Add("user",
            "USER")
        }).RequireKeysThroughRollout(["password"]).RequireKeysThroughRollout(["user"]);
        var secret = NewSecret().Bind(binding, DateTime.UtcNow);
        var changed = binding with { KeyMappings = binding.KeyMappings.SetItem("password", "NEW") };
        var rejected = Assert.IsType<SecretBindingKeysInUse>(secret.DeclareBinding(changed, DateTime.UtcNow).Value);
        Assert.Equal(["password"], rejected.Keys.ToArray());
        var changedConsumer = Assert.IsType<SecretBindingConsumerChanged>(
            secret.DeclareBinding(binding with { Consumer = binding.Consumer with { ResourceId = "other" } },
                DateTime.UtcNow).Value);
        Assert.Equal(binding.Consumer, changedConsumer.Current);
        Assert.Equal("other", changedConsumer.Desired.ResourceId);
        Assert.Same(secret, secret.DeclareBinding(binding, DateTime.UtcNow).Value);
        var released = secret.ReleaseRetainedKeys(binding.Id);
        var updated = Assert.IsType<Secret>(released.DeclareBinding(changed, DateTime.UtcNow).Value);
        Assert.Equal("NEW", Assert.Single(updated.Bindings).KeyMappings["password"]);
        Assert.Equal(["user"], Assert.Single(updated.Bindings).Pins.Required.ToArray());
        Assert.Empty(Assert.Single(updated.Bindings).Pins.Retained);
    }

    [Fact]
    public void Binding_declaration_failures_carry_the_conflicting_consumer_keys_or_limit()
    {
        var now = DateTime.UtcNow;
        var bound = NewSecret().Bind(Binding("existing", "consumer"), now);
        var duplicate = Assert.IsType<SecretBindingConsumerConflict>(bound.DeclareBinding(Binding("other", "consumer"),
            now).Value);
        Assert.Equal(new SecretConsumerIdentity("pole/web-service", "consumer"), duplicate.Consumer);

        var unknown = Binding("other", "other") with
        {
            KeyMappings = ImmutableDictionary<string,
            string>.Empty.Add("missing", "MISSING")
        };
        Assert.Equal(["missing"], Assert.IsType<UnknownSecretBindingKeys>(bound.DeclareBinding(unknown,
            now).Value).Keys.ToArray());

        var full = NewSecret() with
        {
            Bindings = Enumerable.Range(0, Secret.MaximumBindings).Select(index => Binding(index.ToString(),
                index.ToString())).ToImmutableArray()
        };
        Assert.Equal(Secret.MaximumBindings,
            Assert.IsType<SecretBindingCapacityExceeded>(full.DeclareBinding(Binding("extra", "extra"),
            now).Value).Maximum);
    }

    [Fact]
    public void Consecutive_reference_changes_retain_all_earlier_keys_until_release()
    {
        var binding = SecretBinding.Create("a", "pole/web-service", "one", new AllSecretDeployments(), [])
            .RequireKeysThroughRollout(["first"]).RequireKeysThroughRollout(["second"]).RequireKeysThroughRollout([
                "third"]);
        Assert.Equal(["first", "second"], binding.Pins.Retained.ToArray());
        Assert.Equal(["third"], binding.Pins.Required.ToArray());
        Assert.Equal(["first", "second"], binding.RequireKeys([]).Pins.Retained.ToArray());
        Assert.Equal(["second", "third"], binding.RequireKeysThroughRollout(["first"]).Pins.Retained.ToArray());
    }

    [Fact]
    public void RotationPreservesBindingsAndDoesNotMutatePreviousVersion()
    {
        var old = NewSecret()
            .Bind(Binding("a", "one"), DateTime.UtcNow)
            .Bind(Binding("b", "two"), DateTime.UtcNow);
        var rotated = old.Rotate(["password", "user", "token"], DateTime.UtcNow);
        Assert.Equal(1, old.CurrentVersion);
        Assert.Equal(2, rotated.CurrentVersion);
        Assert.Equal(2, rotated.Bindings.Length);
        Assert.Throws<InvalidOperationException>(() => old.Rotate(["user"], DateTime.UtcNow));

        var detached = rotated.Detach("a", DateTime.UtcNow);
        Assert.Equal("two", Assert.Single(detached.Bindings).Consumer.ResourceId);
        Assert.Equal(2, rotated.Bindings.Length);
    }

    [Fact]
    public void BindingRejectsDuplicateConsumerUnknownKeysAndAmbiguousSelection()
    {
        var secret = NewSecret().Bind(Binding("a", "one"), DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => secret.Bind(Binding("b", "one"), DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => new SelectedSecretDeployments([]));
        Assert.Throws<InvalidOperationException>(() => secret.Bind(
            Binding("b", "two") with
            {
                KeyMappings = ImmutableDictionary<string, string>.Empty.Add("missing", "X")
            },
            DateTime.UtcNow));
    }

    [Fact]
    public void MetadataAndEventsHaveNoValueMaterialAndBodiesHaveRedactedDiagnostics()
    {
        var metadata = JsonSerializer.Serialize(SecretMetadata.From(NewSecret()));
        var evt = JsonSerializer.Serialize(new SecretChangedEvent(
            "id",
            2,
            3,
            new SecretRotated(), "Name", EventCore.Create("tenant")));
        foreach (var json in new[] { metadata, evt })
        {
            Assert.DoesNotContain("Ciphertext", json);
            Assert.DoesNotContain("WrappedDataEncryptionKey", json);
            Assert.DoesNotContain("Values", json);
        }

        var body = new RotateSecretBody(1, new()
        {
            ["password"] = "synthetic-sensitive-value"
        });
        Assert.DoesNotContain("synthetic-sensitive-value", body.ToString());
        Assert.DoesNotContain(
            "synthetic-sensitive-value",
            new RotateSecretRequest(ObjectId.GenerateNewId().ToString(), body, "tenant").ToString());
    }

    [Fact]
    public void ValidatorsRejectBadShapesWithoutEchoingValues()
    {
        var id = ObjectId.GenerateNewId().ToString();
        var validator = new RotateSecretValidator();
        var bad = new RotateSecretRequest(
            id,
            new(0, new()
            {
                ["invalid/key"] = "synthetic-sensitive-value"
            }),
            id);
        var errors = validator.Validate(bad);
        Assert.False(errors.IsValid);
        Assert.DoesNotContain(
            "synthetic-sensitive-value",
            string.Join(" ", errors.Errors.Select(x => x.ErrorMessage)));
        Assert.False(new BindSecretValidator().Validate(new BindSecretRequest(
            id,
            new("pole/web-service", id, true, [id], new()),
            id)).IsValid);
        Assert.True(validator.Validate(new RotateSecretRequest(
            id,
            new(1, new()
            {
                ["password"] = "value"
            }),
            id)).IsValid);
    }

    [Fact]
    public void DeploymentSelectionRejectsCaseAliasesOfTheSameIdentity()
    {
        const string id = "507f1f77bcf86cd799439011";
        var request = new BindSecretRequest(
            id,
            new("pole/web-service", id, false, [id, id.ToUpperInvariant()]),
            id);
        Assert.False(new BindSecretValidator().Validate(request).IsValid);
    }

    [Fact]
    public void FactoriesOwnDefaultsAndCollections()
    {
        var keys = new[] { "password", "user" };
        var mappings = new Dictionary<string, string>
        {
            ["password"] = "PASSWORD"
        };
        var ids = new[] { "deployment" };
        var now = DateTime.UtcNow;
        var secret = SecretFactory.Create(
            "s",
            "t",
            "g",
            "i",
            " Name ",
            " purpose ",
            keys,
            now);
        var binding = SecretBinding.Create(
            "b",
            "pole/web-service",
            "c",
            new SelectedSecretDeployments(ids.ToImmutableArray()),
            mappings);
        var bound = secret.Bind(binding, now);
        keys[0] = "missing";
        mappings["missing"] = "NEW";
        ids[0] = "other";
        Assert.Equal("Name", secret.Name);
        Assert.Equal("name", secret.NormalizedName);
        Assert.Equal(1, secret.CurrentVersion);
        Assert.Equal(1, secret.Revision);
        Assert.Equal(SecretLifecycle.Active, secret.Lifecycle);
        Assert.Contains("password", bound.KeyNames);
        Assert.False(Assert.Single(bound.Bindings).KeyMappings.ContainsKey("missing"));
        Assert.Equal("deployment", Assert.Single(((SelectedSecretDeployments)binding.Selection).DeploymentIds));
        var rotatedKeys = new[] { "password", "token" };
        var rotated = bound.Rotate(rotatedKeys, now);
        rotatedKeys[0] = "changed";
        Assert.Contains("password", rotated.KeyNames);
    }

    [Fact]
    public void MongoRoundTripPreservesSelectionVariantsAndImmutableCollections()
    {
        var selectedBinding = SecretBinding.Create(
            "selected",
            "pole/web-service",
            "two",
            new SelectedSecretDeployments(["deployment"]),
            new Dictionary<string, string>
            {
                ["user"] = "USER"
            });
        var original = NewSecret()
            .Bind(Binding("all", "one"), DateTime.UtcNow)
            .Bind(selectedBinding, DateTime.UtcNow);
        var restored = BsonSerializer.Deserialize<Secret>(original.ToBson());
        Assert.Equal<string>(original.KeyNames, restored.KeyNames);
        Assert.IsType<AllSecretDeployments>(restored.Bindings[0].Selection);
        var selected = Assert.IsType<SelectedSecretDeployments>(restored.Bindings[1].Selection);
        Assert.Equal("deployment", Assert.Single(selected.DeploymentIds));
        Assert.Equal("USER", restored.Bindings[1].KeyMappings["user"]);
        Assert.Throws<InvalidOperationException>(() => restored.Rotate(["password"], DateTime.UtcNow));
    }

    [Fact]
    public void Binding_submodels_round_trip_identity_and_all_key_pins()
    {
        var binding = Binding("binding",
            "consumer").RequireKeysThroughRollout(["password"]).RequireKeysThroughRollout(["user"]);
        var restored = BsonSerializer.Deserialize<SecretBinding>(binding.ToBson());
        Assert.Equal(binding.Consumer, restored.Consumer);
        Assert.Equal(binding.Pins.Required.ToArray(), restored.Pins.Required.ToArray());
        Assert.Equal(binding.Pins.Retained.ToArray(), restored.Pins.Retained.ToArray());
        Assert.Equal(["password", "user"], restored.Pins.ProtectedKeys.Order().ToArray());
        Assert.Empty(Binding("fresh", "other").Pins.ProtectedKeys);
    }

    [Fact]
    public void EventJsonRoundTripPreservesEveryChangeVariant()
    {
        SecretChange[] changes =
        [
            new SecretCreated(),
            new SecretRotated(),
            new SecretMetadataUpdated(),
            new SecretDeletedChange(),
            new SecretBindingAdded("binding"),
            new SecretBindingRemoved("binding")
        ];
        foreach (var change in changes)
        {
            var original = new SecretChangedEvent("secret", 2, 3, change, "database", EventCore.Create("tenant"));
            var restored = JsonSerializer.Deserialize<SecretChangedEvent>(
                JsonSerializer.Serialize(original, EventExtensions.SerializerOptions),
                EventExtensions.SerializerOptions);
            Assert.NotNull(restored);
            Assert.Equal(original, restored);
            Assert.Equal("database", restored.Name);
            Assert.Equal("tenant", restored.EventCore.TenantId);
            var json = JsonSerializer.Serialize(original, EventExtensions.SerializerOptions);
            using var payload = JsonDocument.Parse(json);
            Assert.Equal(new[] { "change", "eventCore", "name", "revision", "secretId", "version" },
                payload.RootElement.EnumerateObject().Select(item => item.Name).Order().ToArray());
            Assert.DoesNotContain("ciphertext", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("values", json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
