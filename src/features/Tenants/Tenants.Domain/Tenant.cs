// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.Core.Multitenancy;

namespace Pole.Features.Tenants.Domain;

public sealed record Tenant(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string Name,
    DateTime CreatedAtUtc,
    ImmutableList<TenantMember> Members,
    long ReferenceFence
);

public sealed record TenantMember(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string UserId,
    string Email,
    TenantRole Role,
    DateTime CreatedAtUtc,
    bool IsDefault
);

public static class TenantFactory
{
    public static Tenant Create(string name, string? id = null) => new(
        Id: id ?? ObjectId.GenerateNewId().ToString(),
        Name: name,
        CreatedAtUtc: DateTime.UtcNow,
        Members: [],
        ReferenceFence: 0);

    public static TenantMember CreateMember(
        string tenantId,
        string userId,
        string email,
        TenantRole role,
        bool isDefault = false) => new(
        Id: ObjectId.GenerateNewId().ToString(),
        TenantId: tenantId,
        UserId: userId,
        Email: email,
        Role: role,
        CreatedAtUtc: DateTime.UtcNow,
        IsDefault: isDefault);
}
