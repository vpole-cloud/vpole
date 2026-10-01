using System.Collections.Concurrent;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Primitives;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed class PrivateAccessConnectorRegistry(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, PrivateAccessConnectorRegistration> targets = new();

    public PrivateAccessConnectorRegistration Register(
        DeploymentTargetConnectorIdentity identity,
        string fingerprint,
        long authorizedAt)
    {
        var registration = new PrivateAccessConnectorRegistration(identity, fingerprint, authorizedAt);
        targets.AddOrUpdate(identity.TargetId, registration, (_, previous) =>
        {
            previous.Disconnect();
            return registration;
        });
        return registration;
    }

    public PrivateAccessConnectorRegistration? Find(string targetId) =>
        targets.TryGetValue(targetId, out var registration) && IsCurrent(registration)
            ? registration
            : null;

    public bool IsCurrent(PrivateAccessConnectorRegistration registration) =>
        !registration.Disconnected.IsCancellationRequested &&
        time.GetElapsedTime(registration.LastAuthorizationTimestamp) < PrivateAccessLeaseClock.MaximumLease &&
        targets.TryGetValue(registration.Identity.TargetId, out var current) &&
        ReferenceEquals(current, registration);

    public void Remove(PrivateAccessConnectorRegistration registration)
    {
        targets.TryRemove(new KeyValuePair<string, PrivateAccessConnectorRegistration>(
            registration.Identity.TargetId, registration));
        registration.Disconnect();
    }
}

public sealed class PrivateAccessConnectorRegistration(
    DeploymentTargetConnectorIdentity identity,
    string fingerprint,
    long authorizedAt)
{
    private readonly CancellationTokenSource disconnected = new();
    private long authorizedTimestamp = authorizedAt;
    public DeploymentTargetConnectorIdentity Identity { get; } = identity;
    public string Fingerprint { get; } = fingerprint;
    public CancellationToken Disconnected => disconnected.Token;
    public long LastAuthorizationTimestamp => Interlocked.Read(ref authorizedTimestamp);
    public void Authorized(long timestamp) => Interlocked.Exchange(ref authorizedTimestamp, timestamp);
    public void Disconnect() => disconnected.Cancel();
}
