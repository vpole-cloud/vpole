using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Pole.Encryption;
using Pole.Features.Secrets.Contracts;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Managed;

internal sealed class EnvelopePrepareManagedSecret(IEnvelopeEncryptionService encryption)
{
    public async Task<Result<PreparedManagedSecret, ManagedSecretEncryptionUnavailable>> InvokeAsync(
        string tenantId,
        string secretId,
        ManagedSecretOwner owner,
        string name,
        long version,
        IReadOnlyDictionary<string, string> values,
        ManagedSecretProjectionType projectionType,
        CancellationToken ct)
    {
        if (!Endpoints.SecretValuesValidation.Valid(values.ToDictionary()))
            throw new ArgumentException("Managed Secret values are invalid.", nameof(values));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(projectionType) || projectionType == ManagedSecretProjectionType.BasicAuthentication &&
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
                Ok<EncryptedValue> encrypted => new Ok<PreparedManagedSecret>(new(
                    secretId, tenantId, owner, name, projectionType, version, values.Keys.Order().ToImmutableArray(), JsonSerializer.SerializeToUtf8Bytes(encrypted.Value).ToImmutableArray())),
                Err<EnvelopeEncryptionFailed> => new Err<ManagedSecretEncryptionUnavailable>(new())
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
