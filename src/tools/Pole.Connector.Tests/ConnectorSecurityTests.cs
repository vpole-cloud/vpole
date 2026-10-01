using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pole.Connector;
using Xunit;

namespace Pole.Connector.Tests;

public sealed class ConnectorSecurityTests
{
    [Theory]
    [InlineData("http://broker.example")]
    [InlineData("https://user:pass@broker.example")]
    [InlineData("https://broker.example/path")]
    [InlineData("https://broker.example?token=secret")]
    public void BrokerOriginRejectsNonOriginOrInsecureAddress(string address)
    {
        Assert.Throws<ArgumentException>(() => BrokerOrigin.Parse(address));
    }

    [Fact]
    public void BrokerOriginDerivesFixedSecureControlPaths()
    {
        var origin = BrokerOrigin.Parse("https://broker.example:8443");
        Assert.Equal("wss://broker.example:8443/private-access/connectors",
            origin.WebSocketPath("/private-access/connectors").AbsoluteUri);
        Assert.Equal("https://broker.example:8443/private-access/workers",
            origin.ApiPath("/private-access/workers").AbsoluteUri);
    }

    [Fact]
    public async Task CredentialStoreKeepsPrivateKeyLocalWithOwnerOnlyModes()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        var directory = Path.Combine(Path.GetTempPath(), $"pole-connector-test-{Guid.NewGuid():N}");
        try
        {
            var store = ConnectorIdentityStore.Open(directory);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=connector-test", key, HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            await store.WritePendingAsync(certificate, CancellationToken.None);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(store.PendingPath));
            using (var pending = store.LoadPending())
                Assert.True(pending.HasPrivateKey);

            store.PromotePending();
            Assert.False(store.HasPending);
            Assert.True(store.HasActive);
            using var active = store.LoadActive();
            Assert.Equal(certificate.GetCertHashString(), active.GetCertHashString());
            Assert.True(active.HasPrivateKey);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(store.ActivePath));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialStoreRejectsReadableOrRedirectedIdentity()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        var directory = Path.Combine(Path.GetTempPath(), $"pole-connector-test-{Guid.NewGuid():N}");
        try
        {
            var store = ConnectorIdentityStore.Open(directory);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=connector-test", key, HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            await store.WritePendingAsync(certificate, CancellationToken.None);

            File.SetUnixFileMode(store.PendingPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Throws<IOException>(() => store.LoadPending());

            File.Delete(store.PendingPath);
            File.CreateSymbolicLink(store.PendingPath, "/etc/hosts");
            Assert.Throws<IOException>(() => store.LoadPending());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledPendingWriteDoesNotPublishPartialIdentityOrChangeActive()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        var directory = Path.Combine(Path.GetTempPath(), $"pole-connector-test-{Guid.NewGuid():N}");
        try
        {
            var store = ConnectorIdentityStore.Open(directory);
            using var activeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var activeRequest = new CertificateRequest(
                "CN=active", activeKey, HashAlgorithmName.SHA256);
            using var activeCertificate = activeRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            await store.WritePendingAsync(activeCertificate, CancellationToken.None);
            store.PromotePending();

            using var nextKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var nextRequest = new CertificateRequest(
                "CN=next", nextKey, HashAlgorithmName.SHA256);
            using var nextCertificate = nextRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.WritePendingAsync(nextCertificate, cancelled.Token));
            Assert.False(store.HasPending);
            Assert.Equal([store.ActivePath], Directory.GetFiles(directory));
            using var loadedActive = store.LoadActive();
            Assert.Equal(activeCertificate.GetCertHashString(), loadedActive.GetCertHashString());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
