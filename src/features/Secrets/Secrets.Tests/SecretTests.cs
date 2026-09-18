using System.Text.Json;
using MongoDB.Bson;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Endpoints;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Xunit;
namespace Pole.Features.Secrets.Tests;

public sealed class SecretTests
{
    private static Secret NewSecret() => new("secret", "tenant", "group", "scope", "Name", "name", "purpose",
        ["password", "user"], 1, 1, "Active", [], DateTime.UtcNow, DateTime.UtcNow);
    private static SecretBinding Binding(string id, string consumer) => new(id, "pole/web-service", consumer, true, [], new() { ["password"] = "PASSWORD" });

    [Fact]
    public void RotationPreservesBindingsAndDoesNotMutatePreviousVersion()
    {
        var old = NewSecret().Bind(Binding("a", "one"), DateTime.UtcNow).Bind(Binding("b", "two"), DateTime.UtcNow);
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
        Assert.Throws<InvalidOperationException>(() => secret.Bind(Binding("b", "two") with { DeploymentIds = ["id"] }, DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() => secret.Bind(Binding("b", "two") with { KeyMappings = new() { ["missing"] = "X" } }, DateTime.UtcNow));
    }

    [Fact]
    public void MetadataAndEventsHaveNoValueMaterialAndBodiesHaveRedactedDiagnostics()
    {
        var metadata = JsonSerializer.Serialize(SecretMetadata.From(NewSecret()));
        var evt = JsonSerializer.Serialize(new SecretChangedEvent("id", 2, 3, "Rotated", null));
        foreach (var json in new[] { metadata, evt })
        {
            Assert.DoesNotContain("Ciphertext", json);
            Assert.DoesNotContain("WrappedDataEncryptionKey", json);
            Assert.DoesNotContain("Values", json);
        }
        var body = new RotateSecretBody(1, new() { ["password"] = "synthetic-sensitive-value" });
        Assert.DoesNotContain("synthetic-sensitive-value", body.ToString());
        Assert.DoesNotContain("synthetic-sensitive-value", new RotateSecretRequest(ObjectId.GenerateNewId().ToString(), body, "tenant").ToString());
    }

    [Fact]
    public void ValidatorsRejectBadShapesWithoutEchoingValues()
    {
        var id = ObjectId.GenerateNewId().ToString();
        var validator = new RotateSecretValidator();
        var bad = new RotateSecretRequest(id, new(0, new() { ["invalid/key"] = "synthetic-sensitive-value" }), id);
        var errors = validator.Validate(bad);
        Assert.False(errors.IsValid);
        Assert.DoesNotContain("synthetic-sensitive-value", string.Join(" ", errors.Errors.Select(x => x.ErrorMessage)));
        Assert.False(new BindSecretValidator().Validate(new BindSecretRequest(id, new("pole/web-service", id, true, [id], new()), id)).IsValid);
        Assert.True(validator.Validate(new RotateSecretRequest(id, new(1, new() { ["password"] = "value" }), id)).IsValid);
    }
    [Fact]
    public void DeploymentSelectionRejectsCaseAliasesOfTheSameIdentity()
    {
        const string id = "507f1f77bcf86cd799439011";
        var request = new BindSecretRequest(id,
            new("pole/web-service", id, false, [id, id.ToUpperInvariant()]), id);
        Assert.False(new BindSecretValidator().Validate(request).IsValid);
    }

}
