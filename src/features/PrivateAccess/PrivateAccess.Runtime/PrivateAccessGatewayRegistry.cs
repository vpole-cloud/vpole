using System.Collections.Concurrent;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Primitives;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed class PrivateAccessGatewayRegistry(TimeProvider time)
{
    private readonly ConcurrentDictionary<(string TenantId, string TargetId),
        PrivateAccessGatewayRegistration> gateways = new();

    public PrivateAccessGatewayRegistration Register(
        PrivateAccessGatewayIdentity identity,
        string fingerprint,
        PrivateAccessFrameWriter writer,
        long authorizedAt)
    {
        var registration = new PrivateAccessGatewayRegistration(identity, fingerprint, writer, authorizedAt);
        gateways.AddOrUpdate((identity.TenantId, identity.DeploymentTargetId), registration,
            (_, previous) =>
            {
                previous.Disconnect();
                return registration;
            });
        return registration;
    }

    public PrivateAccessGatewayRegistration? Find(string tenantId, string targetId) =>
        gateways.TryGetValue((tenantId, targetId), out var registration) && IsCurrent(registration)
            ? registration
            : null;

    public bool IsCurrent(PrivateAccessGatewayRegistration registration) =>
        !registration.Disconnected.IsCancellationRequested &&
        time.GetElapsedTime(registration.LastAuthorizationTimestamp) < PrivateAccessLeaseClock.MaximumLease &&
        gateways.TryGetValue((registration.Identity.TenantId, registration.Identity.DeploymentTargetId),
            out var current) && ReferenceEquals(current, registration);

    public void Remove(PrivateAccessGatewayRegistration registration)
    {
        gateways.TryRemove(new KeyValuePair<(string, string), PrivateAccessGatewayRegistration>(
            (registration.Identity.TenantId, registration.Identity.DeploymentTargetId), registration));
        registration.Disconnect();
    }
}

public sealed class PrivateAccessGatewayRegistration(
    PrivateAccessGatewayIdentity identity,
    string fingerprint,
    PrivateAccessFrameWriter writer,
    long authorizedAt)
{
    private readonly CancellationTokenSource disconnected = new();
    private readonly TaskCompletionSource controlReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long authorizedTimestamp = authorizedAt;
    public PrivateAccessGatewayIdentity Identity { get; } = identity;
    public string Fingerprint { get; } = fingerprint;
    public CancellationToken Disconnected => disconnected.Token;
    public long LastAuthorizationTimestamp => Interlocked.Read(ref authorizedTimestamp);
    public void Authorized(long timestamp) => Interlocked.Exchange(ref authorizedTimestamp, timestamp);
    public void ControlAccepted() => controlReady.TrySetResult();
    public async ValueTask OpenStreamAsync(string ticket, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, Disconnected);
        await controlReady.Task.WaitAsync(lifetime.Token);
        await PrivateAccessGatewayProtocol.SendOpenStreamAsync(writer, ticket, lifetime.Token);
    }
    public void Disconnect() => disconnected.Cancel();
}
