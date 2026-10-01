using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Runtime;
using Xunit;
using PersistedGateway = Pole.Features.PrivateAccess.Contracts.PrivateAccessGatewayRegistration;

namespace Pole.Features.PrivateAccess.Tests;

public sealed class PrivateAccessGatewayReconcilerTests
{
    [Fact]
    public async Task MissingIdentitySecretRotatesGenerationBeforePersistingNewPin()
    {
        var tenantId = new string('a', 24);
        var targetId = new string('b', 24);
        var identity = new PrivateAccessGatewayIdentity(
            tenantId, targetId, PrivateAccessGatewayIds.StableId(tenantId, targetId), 1, 1);
        var endpoint = new PrivateAccessGatewayEndpointSnapshot(
            new string('c', 24), new string('d', 24), new string('e', 24),
            "runtime-a", "database", 5432, 5432);
        var expiry = DateTime.UtcNow.AddDays(20);
        var current = new PersistedGateway(identity, new string('a', 64), expiry, [endpoint]);
        var ensures = new List<(long Generation, bool HasCandidate)>();
        var saved = new List<(PrivateAccessGatewayIdentity Identity, string Pin)>();
        var reconciler = new PrivateAccessGatewayReconciler(
            _ => Task.FromResult<IReadOnlyList<PrivateAccessGatewayDesiredState>>(
                [new PrivateAccessGatewayDesiredState(tenantId, targetId, [endpoint])]),
            _ => Task.FromResult<IReadOnlyList<PersistedGateway>>([current]),
            (candidate, pin, _, _, _) =>
            {
                saved.Add((candidate, pin));
                return Task.FromResult(true);
            },
            (_, _) => throw new InvalidOperationException("Gateway is still desired."),
            (candidate, _, _, pfx, _) =>
            {
                ensures.Add((candidate.Generation, pfx is { Length: > 0 }));
                var pin = candidate.Generation == 1 ? new string('b', 64) : new string('c', 64);
                return Task.FromResult<EnsurePrivateAccessGatewayOutcome>(
                    new PrivateAccessGatewayReady(pin, new DateTimeOffset(expiry)));
            },
            (_, _, _) => throw new InvalidOperationException("No endpoint should be deleted."),
            (_, _, _) => throw new InvalidOperationException("Gateway is still desired."),
            Options.Create(new PrivateAccessBrokerOptions
            {
                Enabled = true,
                GatewayImage = "test/gateway:1",
                BrokerUri = "https://broker.example",
                BrokerAddresses = ["192.0.2.8"]
            }),
            TimeProvider.System,
            NullLogger<PrivateAccessGatewayReconciler>.Instance);

        await reconciler.RunAsync(CancellationToken.None);

        Assert.Equal([(1L, true), (2L, true)], ensures);
        var registration = Assert.Single(saved);
        Assert.Equal(2, registration.Identity.Generation);
        Assert.Equal(2, registration.Identity.Revision);
        Assert.Equal(new string('c', 64), registration.Pin);
    }
}
