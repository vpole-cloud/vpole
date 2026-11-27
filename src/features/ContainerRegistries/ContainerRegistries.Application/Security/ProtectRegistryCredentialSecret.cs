// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Encryption;
using Pole.Features.ContainerRegistries.Contracts;

namespace Pole.Features.ContainerRegistries.Application.Security;

public union ProtectRegistryCredentialOutcome(EncryptedValue, RegistryCredentialProtectionFailed);

public delegate ValueTask<ProtectRegistryCredentialOutcome> ProtectRegistryCredentialSecret(
    string tenantId,
    string credentialId,
    long secretVersion,
    string secret,
    CancellationToken cancellationToken = default);

public sealed class EnvelopeProtectRegistryCredentialSecret(
    IEnvelopeEncryptionService encryption
)
{
    public async ValueTask<ProtectRegistryCredentialOutcome> InvokeAsync(
        string tenantId,
        string credentialId,
        long secretVersion,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var result = await encryption.EncryptStringAsync(
            secret,
            RegistryCredentialEncryptionContext.Create(tenantId, credentialId, secretVersion),
            cancellationToken);
        return result switch
        {
            EncryptedValue encrypted => encrypted,
            EnvelopeEncryptionFailed => new RegistryCredentialProtectionFailed(),
        };
    }
}

internal static class RegistryCredentialEncryptionContext
{
    internal static EnvelopeEncryptionContext Create(
        string tenantId,
        string credentialId,
        long secretVersion) => EnvelopeEncryptionContext.ForTenant(
        tenantId,
        "container-registry-credential",
        credentialId,
        "secret",
        secretVersion);
}
