using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Workloads;
using PersistedGateway = Pole.Features.PrivateAccess.Contracts.PrivateAccessGatewayRegistration;

namespace Pole.Features.PrivateAccess.Runtime;

public delegate Task<EnsurePrivateAccessGatewayOutcome> EnsurePrivateAccessGateway(
    PrivateAccessGatewayIdentity identity,
    IReadOnlyList<PrivateAccessGatewayEndpoint> endpoints,
    PrivateAccessGatewaySettings settings,
    byte[]? proposedPfx,
    CancellationToken cancellationToken);

public delegate Task<DeletePrivateAccessGatewayOutcome> DeletePrivateAccessGatewayEndpoint(
    PrivateAccessGatewayIdentity identity,
    PrivateAccessGatewayEndpoint endpoint,
    CancellationToken cancellationToken);

public delegate Task<DeletePrivateAccessGatewayOutcome> DeletePrivateAccessGateway(
    PrivateAccessGatewayIdentity identity,
    IReadOnlyList<PrivateAccessGatewayEndpoint> endpoints,
    CancellationToken cancellationToken);

public sealed class PrivateAccessGatewayReconciler(
    LoadPrivateAccessGatewayDesiredStates loadDesired,
    LoadPrivateAccessGatewayRegistrations loadRegistered,
    SavePrivateAccessGatewayRegistration save,
    RemovePrivateAccessGatewayRegistration remove,
    EnsurePrivateAccessGateway ensureGateway,
    DeletePrivateAccessGatewayEndpoint deleteEndpoint,
    DeletePrivateAccessGateway deleteGateway,
    IOptions<PrivateAccessBrokerOptions> configured,
    TimeProvider time,
    ILogger<PrivateAccessGatewayReconciler> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var desired = configured.Value.Enabled
            ? await loadDesired(cancellationToken)
            : [];
        var registered = await loadRegistered(cancellationToken);
        var desiredByKey = desired.Where(state => state.Endpoints.Count > 0)
            .ToDictionary(state => (state.TenantId, state.DeploymentTargetId));
        var registeredByKey = registered.ToDictionary(registration =>
            (registration.Identity.TenantId, registration.Identity.DeploymentTargetId));

        foreach (var state in desiredByKey.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            registeredByKey.TryGetValue((state.TenantId, state.DeploymentTargetId), out var current);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await EnsureAsync(state, current, timeout.Token); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning("Private Access gateway ensure will retry after {FailureType}.",
                    exception.GetType().Name);
            }
        }

        foreach (var current in registered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (desiredByKey.ContainsKey((current.Identity.TenantId, current.Identity.DeploymentTargetId)))
                continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await WithdrawAsync(current, timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning("Private Access gateway cleanup will retry after {FailureType}.",
                    exception.GetType().Name);
            }
        }
    }

    private async Task EnsureAsync(
        PrivateAccessGatewayDesiredState state,
        PersistedGateway? current,
        CancellationToken cancellationToken)
    {
        var identity = current?.Identity ?? new PrivateAccessGatewayIdentity(
            state.TenantId, state.DeploymentTargetId,
            PrivateAccessGatewayIds.StableId(state.TenantId, state.DeploymentTargetId), 1, 1);
        var endpoints = state.Endpoints.Select(ToRuntimeEndpoint).ToArray();
        if (current is not null)
        {
            foreach (var stale in current.Endpoints.Except(state.Endpoints))
            {
                var deleted = await deleteEndpoint(
                    current.Identity, ToRuntimeEndpoint(stale), cancellationToken);
                if (deleted is not PrivateAccessGatewayDeleted)
                    return;
            }
        }

        var rotate = current is not null &&
            current.CertificateNotAfterUtc <= time.GetUtcNow().AddDays(1).UtcDateTime;
        if (rotate)
            identity = NextGeneration(identity);

        // Supplying a candidate also lets Kubernetes recover a missing Secret. If an
        // existing generation adopts it, the changed pin is rotated before persistence.
        byte[]? proposedPfx = CreateIdentityPfx(identity);
        try
        {
            if (current is null || rotate)
            {
                // Persist the target reference and intended identity before writing
                // Kubernetes objects. A failed write can then be retried or cleaned up.
                var reserved = await ReserveAsync(identity, state, proposedPfx,
                    cancellationToken);
                if (reserved is null)
                    return;
                current = reserved;
            }
            else if (!current.Endpoints.SequenceEqual(state.Endpoints))
            {
                // Record every destination before creating its policy. If provisioning
                // fails or the grant disappears, cleanup still knows what to remove.
                if (!await save(
                    new SavePrivateAccessGatewayRegistrationRequest(
                        Identity: identity,
                        CertificateSha256Pin: current.CertificateSha256Pin,
                        CertificateNotAfterUtc: current.CertificateNotAfterUtc,
                        Endpoints: state.Endpoints),
                    cancellationToken: cancellationToken))
                    return;
                identity = identity with { Revision = checked(identity.Revision + 1) };
                current = current with { Identity = identity, Endpoints = state.Endpoints };
            }

            var result = await ensureGateway(identity, endpoints,
                configured.Value.GatewaySettings(), proposedPfx, cancellationToken);
            if (result is PrivateAccessGatewayTargetUnavailable or
                PrivateAccessGatewayIsolationUnavailable)
            {
                await WithdrawAsync(current, cancellationToken);
                return;
            }
            if (result is not PrivateAccessGatewayReady ready)
                return;

            if (!string.Equals(ready.CertificateSha256Pin, current.CertificateSha256Pin,
                    StringComparison.Ordinal))
            {
                // A Secret with a different pin is never adopted under an existing
                // registration. Reserve the next generation before rotating it.
                identity = NextGeneration(identity);
                if (proposedPfx is not null)
                    CryptographicOperations.ZeroMemory(proposedPfx);
                proposedPfx = CreateIdentityPfx(identity);
                var reserved = await ReserveAsync(identity, state, proposedPfx,
                    cancellationToken);
                if (reserved is null)
                    return;
                current = reserved;
                result = await ensureGateway(identity, endpoints,
                    configured.Value.GatewaySettings(), proposedPfx, cancellationToken);
                if (result is not PrivateAccessGatewayReady rotated)
                    return;
                ready = rotated;
            }

            if (!string.Equals(ready.CertificateSha256Pin, current.CertificateSha256Pin,
                    StringComparison.Ordinal))
                return;
            if (!await save(
                new SavePrivateAccessGatewayRegistrationRequest(
                    Identity: identity,
                    CertificateSha256Pin: ready.CertificateSha256Pin,
                    CertificateNotAfterUtc: ready.CertificateNotAfter.UtcDateTime,
                    Endpoints: state.Endpoints),
                cancellationToken: cancellationToken))
                logger.LogWarning("Private Access gateway registration changed concurrently; ensure will retry.");
        }
        finally
        {
            if (proposedPfx is not null)
                CryptographicOperations.ZeroMemory(proposedPfx);
        }
    }

    private async Task WithdrawAsync(
        PersistedGateway current,
        CancellationToken cancellationToken)
    {
        var deleted = await deleteGateway(current.Identity,
            current.Endpoints.Select(ToRuntimeEndpoint).ToArray(), cancellationToken);
        if (deleted is PrivateAccessGatewayDeleted)
            await remove(current.Identity, cancellationToken);
    }

    private async Task<PersistedGateway?> ReserveAsync(
        PrivateAccessGatewayIdentity identity,
        PrivateAccessGatewayDesiredState state,
        byte[] proposedPfx,
        CancellationToken cancellationToken)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12(
            proposedPfx, null, X509KeyStorageFlags.EphemeralKeySet);
        var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256)
            .ToLowerInvariant();
        var expiry = certificate.NotAfter.ToUniversalTime();
        if (!await save(
            new SavePrivateAccessGatewayRegistrationRequest(
                Identity: identity,
                CertificateSha256Pin: pin,
                CertificateNotAfterUtc: expiry,
                Endpoints: state.Endpoints),
            cancellationToken: cancellationToken))
            return null;
        return new PersistedGateway(identity, pin, expiry, state.Endpoints);
    }

    private static PrivateAccessGatewayEndpoint ToRuntimeEndpoint(
        PrivateAccessGatewayEndpointSnapshot endpoint) => new(
            endpoint.EndpointId,
            endpoint.RuntimeCellId,
            endpoint.ResourceDeploymentId,
            endpoint.KubernetesNamespace,
            endpoint.ServiceName,
            NetworkPort.Create(endpoint.ServicePort),
            NetworkPort.Create(endpoint.TargetPort));

    private static PrivateAccessGatewayIdentity NextGeneration(
        PrivateAccessGatewayIdentity identity) => identity with
        {
            Generation = checked(identity.Generation + 1),
            Revision = checked(identity.Revision + 1)
        };

    private byte[] CreateIdentityPfx(PrivateAccessGatewayIdentity identity)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=pole-access-{identity.GatewayId}",
            key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new("1.3.6.1.5.5.7.3.2")
        }, true));
        using var certificate = request.CreateSelfSigned(
            time.GetUtcNow().AddMinutes(-5), time.GetUtcNow().AddDays(30));
        return certificate.Export(X509ContentType.Pfx, string.Empty);
    }
}

internal sealed class PrivateAccessGatewayReconciliationService(
    IServiceScopeFactory scopes,
    ILogger<PrivateAccessGatewayReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PrivateAccessGatewayReconciler>()
                    .RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning("Private Access gateway reconciliation will retry after {FailureType}.",
                    exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
