using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application;

public sealed record SecretMetadata(
    string Id, string TenantId, string ResourceGroupId, string IsolationScopeId,
    string Name, string Purpose, string[] KeyNames, long CurrentVersion, long Revision,
    string Lifecycle, DateTime CreatedAtUtc, DateTime UpdatedAtUtc)
{
    public static SecretMetadata From(Secret secret) => new(secret.Id, secret.TenantId,
        secret.ResourceGroupId, secret.IsolationScopeId, secret.Name, secret.Purpose,
        secret.KeyNames, secret.CurrentVersion, secret.Revision, secret.Lifecycle,
        secret.CreatedAtUtc, secret.UpdatedAtUtc);
}

public sealed record SecretMissing;
public sealed record SecretConflict;
public sealed record SecretReferenceRejected;
public sealed record SecretEncryptionFailed;
public union SecretError(SecretMissing, SecretConflict, SecretReferenceRejected, SecretEncryptionFailed);
public sealed record SecretDeleted;
public union SecretMutationOutcome(SecretMetadata, SecretDeleted, SecretError);
public sealed record SecretUsage(SecretBinding Binding, string[] AffectedDeploymentIds);

public sealed record SecretPage(SecretMetadata[] Items, string? NextCursor);
