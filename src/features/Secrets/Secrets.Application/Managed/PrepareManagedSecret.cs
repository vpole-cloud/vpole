using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Pole.Encryption;
using Pole.Features.Secrets.Contracts;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Managed;

internal sealed class EnvelopePrepareManagedSecret(IEnvelopeEncryptionService encryption)
{
    public async Task<PrepareManagedSecretOutcome> InvokeAsync(
        string tenantId,
        string secretId,
        ManagedSecretOwner owner,
        string name,
        long version,
        IReadOnlyDictionary<string, string> values,
        ManagedSecretKubernetesType kubernetesType,
        CancellationToken ct)
    {
        if (!Endpoints.SecretValuesValidation.Valid(values.ToDictionary()))
            throw new ArgumentException("Managed Secret values are invalid.", nameof(values));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(kubernetesType) || kubernetesType == ManagedSecretKubernetesType.BasicAuthentication &&
            (!values.ContainsKey("username") || !values.ContainsKey("password")))
            throw new ArgumentException("Basic authentication requires username and password keys.", nameof(values));
        tenantId = SecretIdentity.ObjectId(tenantId);
        secretId = SecretIdentity.ObjectId(secretId);
        owner = new ManagedSecretOwner(ResourceTypeId.Create(owner.ResourceType).Value, SecretIdentity.ObjectId(owner.ResourceId));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(values);
        try
        {
            var protectedValues = await encryption.EncryptAsync(bytes,
                EnvelopeEncryptionContext.ForTenant(tenantId, "Secret", secretId, "values", version), ct);
            return protectedValues switch
            {
                EncryptedValue encrypted => new PreparedManagedSecret(
                    secretId, tenantId, owner, name, kubernetesType, version, values.Keys.Order().ToImmutableArray(), JsonSerializer.SerializeToUtf8Bytes(encrypted).ToImmutableArray()),
                EnvelopeEncryptionFailed => new ManagedSecretEncryptionUnavailable()
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
