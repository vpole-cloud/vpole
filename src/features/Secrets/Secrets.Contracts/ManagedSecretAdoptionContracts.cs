using MongoDB.Driver;

namespace Pole.Features.Secrets.Contracts;

public sealed record ManagedSecretIdentityExpectation(
    string Key,
    string ExpectedValue
);

public sealed record ReadManagedSecretAdoptionRequest(
    string TenantId,
    ManagedSecretOwner Owner,
    string ResourceGroupId,
    string IsolationScopeId,
    ManagedSecretIdentityExpectation Identity
);

// Identity is compared inside Secrets. No stored credential value crosses this boundary.
public sealed record ManagedSecretAdoptionSnapshot(
    ReadManagedSecretAdoptionRequest Request,
    string SecretId,
    long Revision,
    long ReferenceFence,
    long ValueVersion
);

public sealed record ManagedSecretIdentityMismatch;

public union ReadManagedSecretAdoptionOutcome(
    ManagedSecretAdoptionSnapshot,
    ManagedSecretIdentityMismatch,
    ManagedSecretUnavailable,
    ManagedSecretEncryptionUnavailable);

public sealed record ManagedSecretAdoptionFenced;

public union FenceManagedSecretAdoptionOutcome(ManagedSecretAdoptionFenced, ManagedSecretChanged);

public delegate Task<ReadManagedSecretAdoptionOutcome> ReadManagedSecretAdoption(
    ReadManagedSecretAdoptionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public delegate Task<FenceManagedSecretAdoptionOutcome> FenceManagedSecretAdoption(
    ManagedSecretAdoptionSnapshot request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
