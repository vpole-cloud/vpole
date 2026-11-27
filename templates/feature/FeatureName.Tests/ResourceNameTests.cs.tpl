// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Events.Abstractions;
using Pole.Features.FeatureName.Domain;
using Pole.Features.FeatureName.Events;
using Pole.Multitenancy.Primitives;
using Xunit;

namespace Pole.Features.FeatureName.Tests;

public sealed class ResourceNameTests
{
    private const string TenantId = "507f1f77bcf86cd799439011";
    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Factory_sets_explicit_initial_values_and_BSON_preserves_identity()
    {
        var resource = Create();
        Assert.Equal(TenantId, Assert.IsAssignableFrom<ITenantOwned>(resource).TenantId);
        Assert.Equal(1, resource.Revision);
        Assert.Equal(new ResourceNameAudit(CreatedAt, CreatedAt), resource.Audit);
        Assert.Equal("Alpha", resource.Details.Name);
        var bson = resource.ToBsonDocument();
        Assert.Equal(BsonType.ObjectId, bson["_id"].BsonType);
        Assert.Equal(BsonType.ObjectId, bson[nameof(ResourceName.TenantId)].BsonType);
        Assert.Equal(resource, BsonSerializer.Deserialize<ResourceName>(bson));
    }

    [Fact]
    public void Update_changes_one_revision_and_preserves_creation_time()
    {
        var resource = Create();
        var updated = resource.UpdateDetails(ResourceNameDetails.Create("Beta", "Changed"), CreatedAt.AddMinutes(1));
        Assert.Equal(2, updated.Revision);
        Assert.Equal(CreatedAt, updated.Audit.CreatedAtUtc);
        Assert.Equal(CreatedAt.AddMinutes(1), updated.Audit.UpdatedAtUtc);
        Assert.Equal("Alpha", resource.Details.Name);
    }

    [Fact]
    public void Normalized_no_op_returns_the_original_instance()
    {
        var resource = Create();
        Assert.Same(
            resource,
            resource.UpdateDetails(ResourceNameDetails.Create(" Alpha ", " Initial "), CreatedAt.AddHours(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Empty_name_is_not_a_valid_domain_value(string name) =>
        Assert.Throws<ArgumentException>(() => ResourceNameDetails.Create(name, "Description"));

    [Fact]
    public void Domain_bounds_are_enforced_without_an_HTTP_validator()
    {
        Assert.Throws<ArgumentException>(() => ResourceNameDetails.Create(new string('x', 81), ""));
        Assert.Throws<ArgumentException>(() => ResourceNameDetails.Create("Alpha", new string('x', 501)));
        Assert.Throws<ArgumentException>(() => ResourceNameFactory.Create(
            "invalid",
            Create().Details,
            new(ObjectId.GenerateNewId().ToString(), CreatedAt)));
    }

    [Fact]
    public void Aggregate_transitions_revalidate_details_and_require_valid_UTC_timestamps()
    {
        var resource = Create();
        Assert.Throws<ArgumentException>(() => resource.UpdateDetails(new("", ""), CreatedAt.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() => ResourceNameFactory.Create(
            TenantId,
            new("", ""),
            new(ObjectId.GenerateNewId().ToString(), CreatedAt)));
        Assert.Throws<ArgumentException>(() => ResourceNameFactory.Create(
            TenantId,
            resource.Details,
            new(ObjectId.GenerateNewId().ToString(), DateTime.SpecifyKind(CreatedAt, DateTimeKind.Unspecified))));
        Assert.Throws<ArgumentException>(() => resource.UpdateDetails(
            new("Beta", "Changed"),
            CreatedAt.AddMinutes(-1)));
    }

    [Fact]
    public void Event_round_trip_retains_tenant_metadata_without_exporting_domain_details()
    {
        var resource = Create();
        var changed = new ResourceNameCreatedEvent(
            resource.Id,
            resource.Details.Name,
            resource.Revision,
            EventCore.Create(resource.TenantId));
        var json = JsonSerializer.Serialize(changed, EventExtensions.SerializerOptions);
        Assert.Equal(
            changed,
            JsonSerializer.Deserialize<ResourceNameCreatedEvent>(json, EventExtensions.SerializerOptions));
        Assert.DoesNotContain("description", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TenantId, changed.EventCore.TenantId);
    }

    private static ResourceName Create() => ResourceNameFactory.Create(
        TenantId,
        ResourceNameDetails.Create(" Alpha ", " Initial "),
        new(ObjectId.GenerateNewId().ToString(), CreatedAt));
}
