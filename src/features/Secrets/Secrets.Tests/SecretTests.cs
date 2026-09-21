using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Events.Abstractions;
using Pole.Features.Secrets.Application;
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
        Assert.Equal("two", Assert.Single(detached.Bindings).ConsumerId);
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
            new SecretRotated()));
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
    public void EventJsonRoundTripPreservesEveryChangeVariant()
    {
        SecretChange[] changes =
        [
            new SecretCreated(),
            new SecretRotated(),
            new SecretDeletedChange(),
            new SecretBindingAdded("binding"),
            new SecretBindingRemoved("binding")
        ];
        foreach (var change in changes)
        {
            var original = new SecretChangedEvent("secret", 2, 3, change);
            var restored = JsonSerializer.Deserialize<SecretChangedEvent>(
                JsonSerializer.Serialize(original, EventExtensions.SerializerOptions),
                EventExtensions.SerializerOptions);
            Assert.NotNull(restored);
            Assert.Equal(change, restored.Change);
        }
    }
}
