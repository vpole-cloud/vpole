using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Encryption;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Primitives;

namespace Pole.Features.Secrets.Application.Bootstrap;

internal sealed class BootstrapSecrets(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretVersion> versions,
    SecretPlanningFence fence,
    IEnvelopeEncryptionService encryption,
    PrepareManagedSecret prepare,
    CreateBoundManagedSecret create)
{
    public async Task<ReadBootstrapSecretOutcome> ReadAsync(BootstrapSecretRequest request,
        IClientSessionHandle session, CancellationToken ct)
    {
        EnsureTransaction(session);
        var existing = await secrets.Find(session, ManagedSecretFilters.Owner(request.TenantId, request.Owner))
            .SingleOrDefaultAsync(ct);
        if (existing is not null)
            return new BootstrapSecretInitialized(existing.Id);

        var source = await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.Source.SecretId))
            .SingleOrDefaultAsync(ct);
        var grant = source?.Bindings.SingleOrDefault(binding =>
            binding.Usage == SecretBindingUsage.Bootstrap &&
            binding.Consumer.ResourceType == request.Owner.ResourceType &&
            binding.Consumer.ResourceId == request.Owner.ResourceId);
        if (source is null || grant is null || source.ResourceGroupId != request.Placement.ResourceGroupId ||
            source.IsolationScopeId != request.Placement.IsolationScopeId ||
            !Projections.SecretProjectionAuthority.IncludesDeployment(grant.Selection, request.Placement.DeploymentId))
            return new BootstrapSecretUnavailable(ConsumerSecretReferenceError.SecretBindingUnavailable);

        if (!source.KeyNames.Contains(request.Source.Key) || !grant.IncludesKey(request.Source.Key))
            return new BootstrapSecretUnavailable(ConsumerSecretReferenceError.SecretKeyUnavailable);

        if (!source.HasValue(request.Source.Key))
            return new BootstrapSecretUnavailable(ConsumerSecretReferenceError.SecretValuesMissing);

        return new BootstrapSecretReady(request,
            new(source.Id, source.Revision, source.ReferenceFence), source.CurrentVersion);
    }

    public async Task<CreateBootstrapSecretOutcome> CreateAsync(BootstrapSecretReady expected,
        IClientSessionHandle session, CancellationToken ct)
    {
        var read = await ReadAsync(expected.Request, session, ct);
        if (read is BootstrapSecretInitialized initialized)
            return initialized;

        if (read is BootstrapSecretUnavailable unavailable)
            return unavailable;

        var current = (BootstrapSecretReady)read.Value;
        if (current.Source != expected.Source || current.ValueVersion != expected.ValueVersion)
            return new BootstrapSecretChanged();

        var request = current.Request;
        var source = await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.Source.SecretId))
            .SingleAsync(ct);
        if (!await fence.FenceAsync(source, session, ct))
            return new BootstrapSecretChanged();

        var version = await versions.Find(session,
                SecretFilters.Version(request.TenantId, source.Id, current.ValueVersion))
            .SingleOrDefaultAsync(ct);
        if (version is null)
            return new BootstrapSecretUnavailable(ConsumerSecretReferenceError.SecretValuesMissing);

        var decrypted = await encryption.DecryptAsync(version.Value,
            EnvelopeEncryptionContext.ForTenant(request.TenantId, "Secret", source.Id, "values", version.Version), ct);
        if (decrypted is not byte[] plaintext)
            return new ManagedSecretEncryptionUnavailable();

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)!;
            if (!values.TryGetValue(request.Source.Key, out var password))
                return new BootstrapSecretUnavailable(ConsumerSecretReferenceError.SecretValuesMissing);

            var credentials = new Dictionary<string, string>
            {
                ["username"] = request.Username,
                ["password"] = password
            };
            if (!Endpoints.SecretValuesValidation.Valid(credentials))
                return new BootstrapSecretValuesTooLarge();

            var prepared = await prepare(new(request.TenantId, request.CredentialId, request.Owner,
                request.CredentialName, 1, credentials, ManagedSecretKubernetesType.BasicAuthentication), ct);
            if (prepared is ManagedSecretEncryptionUnavailable encryptionFailure)
                return encryptionFailure;

            var outcome = await create(new((PreparedManagedSecret)prepared.Value, request.Placement.ResourceGroupId,
                request.Placement.IsolationScopeId, request.Placement.DeploymentId), session, ct);
            return outcome switch
            {
                ManagedSecretMutationSucceeded => new BootstrapSecretInitialized(request.CredentialId),
                ManagedSecretChanged => new BootstrapSecretChanged(),
                ManagedSecretUnavailable =>
                    new BootstrapSecretUnavailable(ConsumerSecretReferenceError.SecretConsumerUnavailable),
                ManagedSecretReferenceRejected =>
                    new BootstrapSecretUnavailable(ConsumerSecretReferenceError.SecretConsumerUnavailable),
                ManagedSecretEncryptionUnavailable error => error
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Secret bootstrap requires the owner mutation transaction.");
    }
}
