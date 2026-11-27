// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using MongoDB.Driver;
using Pole.Encryption;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Managed;

internal sealed class MongoRotateManagedSecret(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretVersion> versions,
    StoreSecretChange publish,
    TimeProvider clock
)
{
    public async Task<ManagedSecretMutationOutcome> InvokeAsync(
        RotateManagedSecretRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction || request.Prepared.Version != checked(request.ExpectedVersion + 1))
            throw new InvalidOperationException(
                "Managed Secret rotation requires a transaction and consecutive version.");
        var current = await secrets.Find(
                session,
                ManagedSecretFilters.Owner(request.Prepared.TenantId, request.Prepared.Owner))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new ManagedSecretUnavailable();
        var kubernetesType
            = ((ProviderManagedSecret)current.Management).KubernetesType == KubernetesSecretType.BasicAuthentication
                ? ManagedSecretKubernetesType.BasicAuthentication
                : ManagedSecretKubernetesType.Opaque;
        if (kubernetesType != request.Prepared.KubernetesType || current.Id != request.Prepared.SecretId ||
            current.CurrentVersion != request.ExpectedVersion || current.Name != request.Prepared.Name ||
            current.Bindings.Any(x =>
                x.KeyMappings.Keys.Concat(x.Pins.ProtectedKeys).Except(request.Prepared.Keys).Any()))
            return new ManagedSecretChanged();
        var now = clock.GetUtcNow().UtcDateTime;
        var rotated = current.Rotate(request.Prepared.Keys, now);
        await secrets.ReplaceOneAsync(
            session,
            SecretFilters.Identity(current.TenantId, current.Id),
            rotated,
            cancellationToken: ct);
        await versions.InsertOneAsync(
            session,
            new SecretVersion(
                $"{current.Id}:{request.Prepared.Version}",
                current.TenantId,
                current.Id,
                request.Prepared.Version,
                JsonSerializer.Deserialize<EncryptedValue>(request.Prepared.EncryptedValues.AsSpan())!,
                now),
            cancellationToken: ct);
        await publish(session, rotated, new SecretRotated(), ct);
        return new ManagedSecretMutationSucceeded();
    }
}
