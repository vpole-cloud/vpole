using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application;

public sealed record SecretMetadata(
    string Id,
    string TenantId,
    string ResourceGroupId,
    string IsolationScopeId,
    string Name,
    string Purpose,
    string[] KeyNames,
    long CurrentVersion,
    long Revision,
    string Lifecycle,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string[] MissingValueKeys,
    GitResourceStatus? GitOps = null,
    SecretManagedOwnerMetadata? ManagedBy = null)
{

    public static SecretMetadata From(Secret secret) => new(
        secret.Id,
        secret.TenantId,
        secret.ResourceGroupId,
        secret.IsolationScopeId,
        secret.Name,
        secret.Purpose,
        secret.KeyNames.ToArray(),
        secret.CurrentVersion,
        secret.Revision,
        secret.Lifecycle.ToString(),
        secret.CreatedAtUtc,
        secret.UpdatedAtUtc,
        secret.KeyNames.Where(key => !secret.HasValue(key)).ToArray(),
        ManagedBy: secret.Management is ProviderManagedSecret owner
            ? new(owner.ResourceType, owner.ResourceId, owner.BindingId)
            : null);
}

public sealed record SecretManagedOwnerMetadata(
    string ResourceType,
    string ResourceId,
    string BindingId);
