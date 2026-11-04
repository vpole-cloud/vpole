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
        PrepareManagedSecretRequest request,
        CancellationToken ct)
    {
        if (!Endpoints.SecretValuesValidation.Valid(request.Values.ToDictionary()))
            throw new ArgumentException("Managed Secret values are invalid.", nameof(request.Values));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        if (!Enum.IsDefined(request.KubernetesType) || request.KubernetesType == ManagedSecretKubernetesType.BasicAuthentication &&
            (!request.Values.ContainsKey("username") || !request.Values.ContainsKey("password")))
            throw new ArgumentException("Basic authentication requires username and password keys.", nameof(request.Values));
        request = request with
        {
            TenantId = SecretIdentity.ObjectId(request.TenantId),
            SecretId = SecretIdentity.ObjectId(request.SecretId),
            Owner = new ManagedSecretOwner(ResourceTypeId.Create(request.Owner.ResourceType).Value, SecretIdentity.ObjectId(request.Owner.ResourceId))
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request.Values);
        try
        {
            var protectedValues = await encryption.EncryptAsync(bytes,
                EnvelopeEncryptionContext.ForTenant(request.TenantId, "Secret", request.SecretId, "values", request.Version), ct);
            return protectedValues switch
            {
                EncryptedValue encrypted => new PreparedManagedSecret(
                    request.SecretId, request.TenantId, request.Owner, request.Name, request.KubernetesType, request.Version, request.Values.Keys.Order().ToImmutableArray(), JsonSerializer.SerializeToUtf8Bytes(encrypted).ToImmutableArray()),
                EnvelopeEncryptionFailed => new ManagedSecretEncryptionUnavailable()
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
