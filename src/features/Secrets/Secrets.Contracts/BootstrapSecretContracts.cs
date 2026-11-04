using MongoDB.Driver;

namespace Pole.Features.Secrets.Contracts;

public sealed record BootstrapSecretSource(
    string SecretId,
    string Key);

public sealed record BootstrapSecretPlacement(
    string ResourceGroupId,
    string IsolationScopeId,
    string DeploymentId);

public sealed record BootstrapSecretRequest(
    string TenantId,
    ManagedSecretOwner Owner,
    BootstrapSecretSource Source,
    BootstrapSecretPlacement Placement,
    string CredentialId,
    string CredentialName,
    string Username);

public sealed record BootstrapSecretInitialized(string CredentialId);

public sealed record BootstrapSecretReady(
    BootstrapSecretRequest Request,
    SecretPlanningVersion Source,
    long ValueVersion);

public sealed record BootstrapSecretUnavailable(ConsumerSecretReferenceError Reason);

public sealed record BootstrapSecretChanged;

public sealed record BootstrapSecretValuesTooLarge;

public union ReadBootstrapSecretOutcome(
    BootstrapSecretInitialized,
    BootstrapSecretReady,
    BootstrapSecretUnavailable);

public union CreateBootstrapSecretOutcome(
    BootstrapSecretInitialized,
    BootstrapSecretUnavailable,
    BootstrapSecretChanged,
    BootstrapSecretValuesTooLarge,
    ManagedSecretEncryptionUnavailable);

/// <summary>Reads only grant/version metadata. Initialized credentials no longer depend on source values.</summary>
public delegate Task<ReadBootstrapSecretOutcome> ReadBootstrapSecret(
    BootstrapSecretRequest request,
    IClientSessionHandle session,
    CancellationToken ct);

/// <summary>Copies an authorized source key inside Secrets and joins the owner mutation transaction.</summary>
public delegate Task<CreateBootstrapSecretOutcome> CreateBootstrapSecret(
    BootstrapSecretReady request,
    IClientSessionHandle session,
    CancellationToken ct);
