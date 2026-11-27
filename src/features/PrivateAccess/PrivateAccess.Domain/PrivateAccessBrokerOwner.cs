// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.PrivateAccess.Domain;

public sealed record PrivateAccessBrokerOwner(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string DeploymentTargetId,
    long GatewayGeneration,
    long GatewayRevision,
    long Epoch,
    string? InstanceId,
    string? InternalOrigin,
    DateTime LeaseExpiresAtUtc
) : ITenantOwned
{
    public PrivateAccessBrokerOwner Reset(long generation, long revision) => this with
    {
        GatewayGeneration = generation,
        GatewayRevision = revision,
        Epoch = checked(Epoch + 1),
        InstanceId = null,
        InternalOrigin = null,
        LeaseExpiresAtUtc = DateTime.UnixEpoch,
    };

    public static void ValidateOrigin(string internalOrigin)
    {
        if (!Uri.TryCreate(internalOrigin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("The broker's internal origin must be HTTPS.", nameof(internalOrigin));
    }
}

public static class PrivateAccessBrokerOwnerFactory
{
    public static PrivateAccessBrokerOwner Create(
        string gatewayId,
        string tenantId,
        string deploymentTargetId,
        long generation,
        long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentTargetId);
        if (generation < 1)
            throw new ArgumentOutOfRangeException(nameof(generation));
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));

        return new PrivateAccessBrokerOwner(
            gatewayId,
            tenantId,
            deploymentTargetId,
            generation,
            revision,
            0,
            null,
            null,
            DateTime.UnixEpoch);
    }
}
