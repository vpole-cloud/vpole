// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Encryption;
using Pole.Features.ContainerRegistries.Application.Mongo;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Credentials;

public sealed class RegistryCredentialRewrapPersistence(
    IMongoCollection<RegistryCredential> credentials
)
{
    internal async Task<IReadOnlyList<RegistryCredential>> LoadBatchAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken) => await credentials
        .Find(ContainerRegistryFilters.CredentialsAfter(cursor))
        .SortBy(credential => credential.Id)
        .Limit(limit)
        .ToListAsync(cancellationToken);

    internal async Task<bool> TryReplaceEnvelopeAsync(
        RegistryCredential credential,
        EncryptedValue rewrappedEnvelope,
        CancellationToken cancellationToken)
    {
        var update = await credentials.UpdateOneAsync(
            ContainerRegistryFilters.CredentialForConcurrentRewrap(credential.Id, credential.EncryptedSecret),
            Builders<RegistryCredential>.Update.Set(item => item.EncryptedSecret, rewrappedEnvelope),
            cancellationToken: cancellationToken);
        return update.ModifiedCount == 1;
    }
}
