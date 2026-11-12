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
using Pole.Features.Secrets.Primitives;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Bindings;

internal sealed class BackupStorageSecrets(
    IMongoCollection<Secret> secrets,
    LoadSecretById load,
    LoadSecretVersion versions,
    IEnvelopeEncryptionService encryption,
    FenceRegisteredResource fenceDestination,
    AdmitResourceScopeReference admitScope,
    StoreSecretChange publish,
    TimeProvider clock
)
{
    private static readonly ResourceTypeId Consumer = ResourceTypeId.Create(SecretBinding.BackupDestinationConsumer);

    public async Task<GrantBackupStorageSecretOutcome> GrantAsync(
        BackupStorageSecretAccess request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Grant requires a destination transaction.");
        var secret = await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.SecretId))
            .SingleOrDefaultAsync(ct);
        if (secret is null || !Matches(secret, request) ||
            await fenceDestination(new(request.TenantId, Consumer, request.DestinationId), session, ct) is null ||
            await admitScope(
                new(
                    request.TenantId,
                    request.ResourceGroupId,
                    request.IsolationScopeId,
                    new(Consumer, request.DestinationId)),
                session,
                ct) is ResourceScopeReferenceUnavailable)
            return new BackupStorageSecretDenied();
        var existing = secret.Bindings.SingleOrDefault(x =>
            x.Consumer == new SecretConsumerIdentity(Consumer.Value, request.DestinationId));
        if (existing is not null)
            return Grants(existing, request) ? new BackupStorageSecretGranted() : new BackupStorageSecretDenied();
        if (secret.Bindings.Length >= Secret.MaximumBindings)
            return new BackupStorageSecretDenied();
        var keys = new Dictionary<string, string>
        {
            [request.AccessKeyIdKey] = request.AccessKeyIdKey,
            [request.SecretAccessKeyKey] = request.SecretAccessKeyKey
        };
        var grant = SecretBinding.Create(
            ObjectId.GenerateNewId().ToString(),
            Consumer.Value,
            request.DestinationId,
            new AllSecretDeployments(),
            keys,
            SecretBindingUsage.BackupStorage);
        var updated = secret.Bind(grant, clock.GetUtcNow().UtcDateTime);
        await secrets.ReplaceOneAsync(
            session,
            SecretFilters.Identity(request.TenantId, secret.Id),
            updated,
            cancellationToken: ct);
        await publish(session, updated, new SecretBindingAdded(grant.Id), ct);
        return new BackupStorageSecretGranted();
    }

    public async Task<ReadBackupStorageSecretOutcome> ReadAsync(BackupStorageSecretAccess request, CancellationToken ct)
    {
        var secret = await load(request.TenantId, request.SecretId, ct);
        if (secret is null || !Matches(secret, request) || !secret.Bindings.Any(b => Grants(b, request)))
            return new BackupStorageSecretDenied();
        var version = await versions(request.TenantId, secret.Id, secret.CurrentVersion, ct);
        if (version is null)
            return new BackupStorageSecretDenied();
        var result = await encryption.DecryptAsync(
            version.Value,
            EnvelopeEncryptionContext.ForTenant(request.TenantId, "Secret", secret.Id, "values", version.Version),
            ct);
        if (result is not byte[] plaintext)
            return new ManagedSecretEncryptionUnavailable();
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (!root.TryGetProperty(request.AccessKeyIdKey, out var id) ||
                !root.TryGetProperty(request.SecretAccessKeyKey, out var key))
                return new BackupStorageSecretDenied();
            return new BackupStorageCredentials(id.GetString()!, key.GetString()!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static bool Matches(Secret secret, BackupStorageSecretAccess request) =>
        secret.Management is not ProviderManagedSecret && secret.ResourceGroupId == request.ResourceGroupId &&
        secret.IsolationScopeId == request.IsolationScopeId && request.AccessKeyIdKey != request.SecretAccessKeyKey &&
        secret.HasValue(request.AccessKeyIdKey) && secret.HasValue(request.SecretAccessKeyKey);

    private static bool Grants(SecretBinding binding, BackupStorageSecretAccess request) =>
        binding.GrantsRuntimeProjection() && binding.Consumer.ResourceType == Consumer.Value &&
        binding.Consumer.ResourceId == request.DestinationId && binding.IncludesKey(request.AccessKeyIdKey) &&
        binding.IncludesKey(request.SecretAccessKeyKey);
}
