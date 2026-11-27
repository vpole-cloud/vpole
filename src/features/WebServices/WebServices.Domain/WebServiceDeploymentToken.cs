// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.WebServices.Domain;

public sealed record WebServiceDeploymentToken(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string WebServiceId,
    string Name,
    string TokenPrefix,
    string TokenHash,
    ImmutableHashSet<string> AllowedOperations,
    DateTime CreatedAtUtc,
    string? CreatedByUserId,
    string? CreatedByEmail,
    DateTime? RevokedAtUtc,
    string? RevokedByUserId,
    DateTime? LastUsedAtUtc,
    string? LastUsedSource
) : ITenantOwned;

public static class WebServiceDeploymentTokenOperations
{
    public const string UpdateImageTag = nameof(UpdateImageTag);
}

public static class WebServiceDeploymentTokenFactory
{
    public const string RawTokenPrefix = "pole_ci_";

    public static CreateOptions DefaultOptions =>
        new(
            Id: ObjectId.GenerateNewId().ToString(),
            CreatedAtUtc: DateTime.UtcNow,
            AllowedOperations: [WebServiceDeploymentTokenOperations.UpdateImageTag]);

    public static IssuedToken Issue(
        string tenantId,
        string webServiceId,
        string name,
        string? createdByUserId,
        string? createdByEmail,
        CreateOptions options)
    {
        var rawToken = GenerateRawToken();
        var tokenPrefix = rawToken[..Math.Min(20, rawToken.Length)];
        var tokenHash = Hash(rawToken);

        return new IssuedToken(
            new WebServiceDeploymentToken(
                options.Id,
                tenantId,
                webServiceId,
                name,
                tokenPrefix,
                tokenHash,
                options.AllowedOperations,
                options.CreatedAtUtc,
                createdByUserId,
                createdByEmail,
                RevokedAtUtc: null,
                RevokedByUserId: null,
                LastUsedAtUtc: null,
                LastUsedSource: null),
            rawToken);
    }

    public static bool IsRawDeploymentToken(string? rawToken) => !string.IsNullOrWhiteSpace(rawToken) &&
        rawToken.StartsWith(RawTokenPrefix, StringComparison.Ordinal);

    public static string Hash(string rawToken)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string GenerateRawToken() =>
        $"{RawTokenPrefix}{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";

    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc,
        ImmutableHashSet<string> AllowedOperations
    );

    public sealed record IssuedToken(
        WebServiceDeploymentToken Token,
        string RawToken
    );
}
