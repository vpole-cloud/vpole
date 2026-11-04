using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Encryption;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed class ManagedSecretAdoption(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretVersion> versions,
    IEnvelopeEncryptionService encryption)
{
    public async Task<ReadManagedSecretAdoptionOutcome> ReadAsync(
        ReadManagedSecretAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        RequireTransaction(session);
        var current = await secrets.Find(session, ManagedSecretFilters.Owner(request.TenantId, request.Owner))
            .FirstOrDefaultAsync(ct);
        if (current is null || current.ResourceGroupId != request.ResourceGroupId ||
            current.IsolationScopeId != request.IsolationScopeId ||
            current.Management is not ProviderManagedSecret owner ||
            !current.Bindings.Any(binding => binding.Id == owner.BindingId &&
                binding.Consumer.ResourceType == owner.ResourceType &&
                binding.Consumer.ResourceId == owner.ResourceId) || !current.HasValue(request.Identity.Key))
            return new ManagedSecretUnavailable();

        var version = await versions.Find(session,
            SecretFilters.Version(current.TenantId, current.Id, current.CurrentVersion)).FirstOrDefaultAsync(ct);
        if (version is null)
            return new ManagedSecretUnavailable();

        var decrypted = await encryption.DecryptAsync(version.Value,
            EnvelopeEncryptionContext.ForTenant(current.TenantId, "Secret", current.Id, "values", version.Version), ct);
        if (decrypted is not byte[] bytes)
            return new ManagedSecretEncryptionUnavailable();

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty(request.Identity.Key, out var identity) ||
                identity.ValueKind != JsonValueKind.String || identity.GetString() != request.Identity.ExpectedValue)
                return new ManagedSecretIdentityMismatch();

            return new ManagedSecretAdoptionSnapshot(request, current.Id, current.Revision,
                current.ReferenceFence, current.CurrentVersion);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<FenceManagedSecretAdoptionOutcome> FenceAsync(
        ManagedSecretAdoptionSnapshot request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        RequireTransaction(session);
        var filter = ManagedSecretFilters.Adoption(request);
        var result = await secrets.UpdateOneAsync(session, filter,
            Builders<Secret>.Update.Inc(item => item.ReferenceFence, 1), cancellationToken: ct);
        return result.ModifiedCount == 1 ? new ManagedSecretAdoptionFenced() : new ManagedSecretChanged();
    }

    private static void RequireTransaction(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Managed credential adoption requires the owner transaction.");
    }
}
