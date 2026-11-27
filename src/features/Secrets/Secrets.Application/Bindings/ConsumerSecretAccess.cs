// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Encryption;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Bindings;

internal sealed class ConsumerSecretAccess(
    IMongoCollection<Secret> secrets,
    LoadSecretById load,
    LoadSecretVersion versions,
    IEnvelopeEncryptionService encryption,
    FenceRegisteredResource fenceConsumer,
    AdmitResourceScopeReference admitScope,
    SecretBindingConsumerCatalog consumers,
    StoreSecretChange publish,
    TimeProvider clock
)
{
    public async Task<GrantConsumerSecretAccessOutcome> GrantAsync(
        ConsumerSecretAccessRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Secret access grants require a consumer transaction.");

        var capability = consumers.Find(request.ConsumerType);
        var secret = await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.SecretId))
            .SingleOrDefaultAsync(ct);
        if (capability?.ValueAccess != SecretConsumerValueAccess.GrantedKeys || secret is null ||
            !Matches(secret, request) ||
            await fenceConsumer(new(request.TenantId, request.ConsumerType, request.ConsumerId), session, ct) is null ||
            await admitScope(
                new(
                    request.TenantId,
                    request.ResourceGroupId,
                    request.IsolationScopeId,
                    new(request.ConsumerType, request.ConsumerId)),
                session,
                ct) is ResourceScopeReferenceUnavailable)
            return new ConsumerSecretAccessDenied();

        var existing = secret.Bindings.SingleOrDefault(binding =>
            binding.Consumer == new SecretConsumerIdentity(request.ConsumerType.Value, request.ConsumerId));
        if (existing is not null)
            return Grants(existing, request, capability)
                ? new ConsumerSecretAccessGranted()
                : new ConsumerSecretAccessDenied();
        if (secret.Bindings.Length >= Secret.MaximumBindings)
            return new ConsumerSecretAccessDenied();

        var grant = SecretBinding.Create(
            ObjectId.GenerateNewId().ToString(),
            request.ConsumerType.Value,
            request.ConsumerId,
            new AllSecretDeployments(),
            request.Keys.ToDictionary(key => key, key => key),
            capability.Usage);
        var updated = secret.Bind(grant, clock.GetUtcNow().UtcDateTime);
        await secrets.ReplaceOneAsync(
            session,
            SecretFilters.Identity(request.TenantId, secret.Id),
            updated,
            cancellationToken: ct);
        await publish(session, updated, new SecretBindingAdded(grant.Id), ct);
        return new ConsumerSecretAccessGranted();
    }

    public async Task<ReadConsumerSecretValuesOutcome> ReadAsync(
        ConsumerSecretAccessRequest request,
        CancellationToken ct)
    {
        var capability = consumers.Find(request.ConsumerType);
        var secret = await load(request.TenantId, request.SecretId, ct);
        if (capability?.ValueAccess != SecretConsumerValueAccess.GrantedKeys || secret is null ||
            !Matches(secret, request) || !secret.Bindings.Any(binding => Grants(binding, request, capability)))
            return new ConsumerSecretAccessDenied();

        var version = await versions(request.TenantId, secret.Id, secret.CurrentVersion, ct);
        if (version is null)
            return new ConsumerSecretAccessDenied();
        var result = await encryption.DecryptAsync(
            version.Value,
            EnvelopeEncryptionContext.ForTenant(request.TenantId, "Secret", secret.Id, "values", version.Version),
            ct);
        if (result is not byte[] plaintext)
            return new ManagedSecretEncryptionUnavailable();

        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var selected = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var key in request.Keys)
            {
                if (!document.RootElement.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                    return new ConsumerSecretAccessDenied();
                selected.Add(key, value.GetString()!);
            }

            return new ConsumerSecretValues(selected.ToImmutable());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static bool Matches(Secret secret, ConsumerSecretAccessRequest request) =>
        secret.Management is not ProviderManagedSecret && secret.ResourceGroupId == request.ResourceGroupId &&
        secret.IsolationScopeId == request.IsolationScopeId && !request.Keys.IsDefaultOrEmpty &&
        request.Keys.Length <= 256 && request.Keys.Distinct(StringComparer.Ordinal).Count() == request.Keys.Length &&
        request.Keys.All(secret.HasValue);

    private bool Grants(
        SecretBinding binding,
        ConsumerSecretAccessRequest request,
        SecretDeclarationConsumer capability) => binding.Consumer.ResourceType == request.ConsumerType.Value &&
        binding.Consumer.ResourceId == request.ConsumerId && binding.Usage == capability.Usage &&
        consumers.GrantsProjection(binding) && request.Keys.All(binding.IncludesKey);
}
