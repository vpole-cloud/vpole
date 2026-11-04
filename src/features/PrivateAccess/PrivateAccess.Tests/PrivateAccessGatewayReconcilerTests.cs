using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    public async Task MissingIdentitySecret_ReservesNextGenerationBeforeKubernetesRotation()
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
(request, _) =>
            {
                saved.Add((request.Identity, request.CertificateSha256Pin));
                return Task.FromResult(true);
            },
            (_, _) => throw new InvalidOperationException("Gateway is still desired."),
            (candidate, _, _, pfx, _) =>
            {
                ensures.Add((candidate.Generation, pfx is { Length: > 0 }));
                if (candidate.Generation == 2)
                    Assert.Equal(2, Assert.Single(saved).Identity.Generation);
                var pin = candidate.Generation == 1
                    ? new string('b', 64)
                    : CertificatePin(pfx!);
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
        Assert.Equal(2, saved.Count);
        var registration = saved[0];
        Assert.Equal(2, registration.Identity.Generation);
        Assert.Equal(2, registration.Identity.Revision);
        Assert.Equal(registration.Pin, saved[1].Pin);
    }

    [Fact]
    public async Task NewGateway_ReservesCleanupRecordBeforeProvisioning()
    {
        var tenantId = new string('a', 24);
        var targetId = new string('b', 24);
        var endpoint = new PrivateAccessGatewayEndpointSnapshot(
            "redis", new string('c', 24), new string('d', 24),
            "runtime-a", "redis", 6379, 6379);
        var reserved = false;
        var reconciler = new PrivateAccessGatewayReconciler(
            _ => Task.FromResult<IReadOnlyList<PrivateAccessGatewayDesiredState>>(
                [new PrivateAccessGatewayDesiredState(tenantId, targetId, [endpoint])]),
            _ => Task.FromResult<IReadOnlyList<PersistedGateway>>([]),
(request, _) =>
            {
                Assert.Equal(64, request.CertificateSha256Pin.Length);
                Assert.Equal(endpoint, Assert.Single(request.Endpoints));
                reserved = true;
                return Task.FromResult(true);
            },
            (_, _) => throw new InvalidOperationException(),
            (_, _, _, pfx, _) =>
            {
                Assert.True(reserved);
                return Task.FromResult<EnsurePrivateAccessGatewayOutcome>(
                    new PrivateAccessGatewayReady(
                        CertificatePin(pfx!), DateTimeOffset.UtcNow.AddDays(30)));
            },
            (_, _, _) => throw new InvalidOperationException(),
            (_, _, _) => throw new InvalidOperationException(),
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

        Assert.True(reserved);
    }

    [Fact]
    public async Task UnavailableTarget_WithdrawsExistingGatewayEvenWithActiveGrant()
    {
        var tenantId = new string('a', 24);
        var targetId = new string('b', 24);
        var identity = new PrivateAccessGatewayIdentity(
            tenantId, targetId, PrivateAccessGatewayIds.StableId(tenantId, targetId), 1, 1);
        var endpoint = new PrivateAccessGatewayEndpointSnapshot(
            "redis", new string('c', 24), new string('d', 24),
            "runtime-a", "redis", 6379, 6379);
        var current = new PersistedGateway(identity, new string('a', 64),
            DateTime.UtcNow.AddDays(20), [endpoint]);
        var deleted = false;
        var removed = false;
        var reconciler = new PrivateAccessGatewayReconciler(
            _ => Task.FromResult<IReadOnlyList<PrivateAccessGatewayDesiredState>>(
                [new PrivateAccessGatewayDesiredState(tenantId, targetId, [endpoint])]),
            _ => Task.FromResult<IReadOnlyList<PersistedGateway>>([current]),
(request, _) => throw new InvalidOperationException(),
            (_, _) =>
            {
                Assert.True(deleted);
                removed = true;
                return Task.FromResult(true);
            },
            (_, _, _, _, _) => Task.FromResult<EnsurePrivateAccessGatewayOutcome>(
                new PrivateAccessGatewayTargetUnavailable()),
            (_, _, _) => throw new InvalidOperationException(),
            (_, routes, _) =>
            {
                Assert.Equal(endpoint.EndpointId, Assert.Single(routes).EndpointId);
                deleted = true;
                return Task.FromResult<DeletePrivateAccessGatewayOutcome>(
                    new PrivateAccessGatewayDeleted());
            },
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

        Assert.True(removed);
    }

    [Fact]
    public async Task FailedEndpointInventoryWrite_DoesNotProvisionOrOrphanNewPolicy()
    {
        var tenantId = new string('a', 24);
        var targetId = new string('b', 24);
        var identity = new PrivateAccessGatewayIdentity(
            tenantId, targetId, PrivateAccessGatewayIds.StableId(tenantId, targetId), 1, 1);
        var first = new PrivateAccessGatewayEndpointSnapshot(
            "redis", new string('c', 24), new string('d', 24),
            "runtime-a", "redis-a", 6379, 6379);
        var second = first with
        {
            ResourceDeploymentId = new string('e', 24),
            ServiceName = "redis-b"
        };
        var expiry = DateTime.UtcNow.AddDays(20);
        var current = new PersistedGateway(identity, new string('a', 64), expiry, [first]);
        IReadOnlyList<PrivateAccessGatewayEndpointSnapshot> desiredEndpoints = [first, second];
        var ensured = new List<IReadOnlyList<PrivateAccessGatewayEndpoint>>();
        var reconciler = new PrivateAccessGatewayReconciler(
            _ => Task.FromResult<IReadOnlyList<PrivateAccessGatewayDesiredState>>(
                [new PrivateAccessGatewayDesiredState(tenantId, targetId, desiredEndpoints)]),
            _ => Task.FromResult<IReadOnlyList<PersistedGateway>>([current]),
(request, _) => Task.FromResult(request.Endpoints.Count == 1),
            (_, _) => throw new InvalidOperationException(),
            (_, routes, _, _, _) =>
            {
                ensured.Add(routes);
                return Task.FromResult<EnsurePrivateAccessGatewayOutcome>(
                    new PrivateAccessGatewayReady(current.CertificateSha256Pin,
                        new DateTimeOffset(expiry)));
            },
            (_, _, _) => throw new InvalidOperationException(),
            (_, _, _) => throw new InvalidOperationException(),
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
        Assert.Empty(ensured);

        desiredEndpoints = [first];
        await reconciler.RunAsync(CancellationToken.None);
        Assert.Equal("redis-a", Assert.Single(Assert.Single(ensured)).ServiceName);
    }

    private static string CertificatePin(byte[] pfx)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12(
            pfx, null, X509KeyStorageFlags.EphemeralKeySet);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256)
            .ToLowerInvariant();
    }
}
