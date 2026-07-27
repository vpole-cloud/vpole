using System.Reflection;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using k8s;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Pole.Encryption;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

public sealed class DeploymentTargetConnectionSecurityTests
{
    private const string KubernetesServer = "https://cluster.pole.test:6443";
    private const string ServiceAccountServer = "https://service-account.pole.test:6443";
    private const string KubeconfigToken = "pole-kubeconfig-secret-token-7a91";
    private const string ServiceAccountToken = "pole-service-account-secret-token-4b62";

    private static readonly Lazy<string> CertificateAuthorityData =
        new(() => CreateCertificateAuthorityData("pole-test-ca"));

    private static readonly Lazy<string> AlternateCertificateAuthorityData =
        new(() => CreateCertificateAuthorityData("pole-alternate-test-ca"));

    [Fact]
    public async Task Kubeconfig_PrepareAndResolve_UsesEncryptedCanonicalCredentialAndHardenedTransport()
    {
        using var encryption = TestEncryptionScope.Create();
        var targetId = ObjectId.GenerateNewId().ToString();
        var kubeconfig = CreateKubeconfig(KubeconfigToken);

        var prepared = AssertSuccess(await encryption.ConnectionService.PrepareAsync(
            targetId,
            connectionVersion: 3,
            new KubeconfigConnectionInput(kubeconfig)));
        using (prepared)
        {
            var persisted = Assert.IsType<KubeconfigDeploymentTargetConnection>(prepared.Connection);
            Assert.Equal(3, persisted.Version);
            Assert.Equal("pole-context", persisted.ContextName);
            Assert.Equal(KubernetesServer, persisted.ServerUrl);
            Assert.NotEmpty(persisted.EncryptedKubeconfig.Ciphertext);
            Assert.Equal(
                LocalKeyFileKeyEncryptionProvider.StableProviderId,
                persisted.EncryptedKubeconfig.KeyProviderId);
            Assert.Equal(
                EnvelopeEncryptionService.CurrentFormatVersion,
                persisted.EncryptedKubeconfig.FormatVersion);

            AssertHardenedTransport(prepared.Candidate, KubernetesServer);

            var resolved = AssertSuccess(await encryption.ConnectionService.CreateAsync(
                targetId,
                persisted));
            using (resolved)
            {
                AssertHardenedTransport(resolved, KubernetesServer);
            }
        }
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("http")]
    [InlineData("insecure")]
    [InlineData("file")]
    [InlineData("basic")]
    [InlineData("mixed")]
    public async Task Kubeconfig_Prepare_RejectsUnsafeConfiguration(string variant)
    {
        using var encryption = TestEncryptionScope.Create();
        var kubeconfig = CreateUnsafeKubeconfig(variant);

        var result = await encryption.ConnectionService.PrepareAsync(
            ObjectId.GenerateNewId().ToString(),
            connectionVersion: 1,
            new KubeconfigConnectionInput(kubeconfig));

        var failure = AssertFailure(result);
        Assert.IsType<ValidationFailure>(failure);
        AssertSafeFailure(failure, KubeconfigToken, "pole-basic-password", "pole-should-never-run");
    }

    [Fact]
    public async Task PreparedConnection_AndApiDto_DoNotExposeKubeconfigPlaintext()
    {
        using var encryption = TestEncryptionScope.Create();
        var targetId = ObjectId.GenerateNewId().ToString();
        var kubeconfig = CreateKubeconfig(KubeconfigToken);
        var prepared = AssertSuccess(await encryption.ConnectionService.PrepareAsync(
            targetId,
            connectionVersion: 1,
            new KubeconfigConnectionInput(kubeconfig)));

        using (prepared)
        {
            var persisted = Assert.IsType<KubeconfigDeploymentTargetConnection>(prepared.Connection);
            var persistedJson = persisted.ToBsonDocument().ToJson();
            Assert.DoesNotContain(KubeconfigToken, persistedJson, StringComparison.Ordinal);
            Assert.DoesNotContain(CertificateAuthorityData.Value, persistedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("apiVersion:", persistedJson, StringComparison.Ordinal);
            Assert.DoesNotContain(KubeconfigToken, persisted.ToString(), StringComparison.Ordinal);

            var dto = CreateTarget(targetId, persisted).ToDto();
            var apiJson = JsonSerializer.Serialize(dto);
            Assert.DoesNotContain(KubeconfigToken, apiJson, StringComparison.Ordinal);
            Assert.DoesNotContain(CertificateAuthorityData.Value, apiJson, StringComparison.Ordinal);
            Assert.DoesNotContain("encrypted", apiJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("serverUrl", apiJson, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(["Kind", "Version"], dto.Connection.GetType().GetProperties().Select(property => property.Name));
        }
    }

    [Fact]
    public async Task ServiceAccount_PrepareAndResolve_EncryptsTheDeliveryBoundCredential()
    {
        using var encryption = TestEncryptionScope.Create();
        var targetId = ObjectId.GenerateNewId().ToString();
        var prepared = AssertSuccess(await encryption.ConnectionService.PrepareAsync(
            targetId,
            connectionVersion: 5,
            new ServiceAccountConnectionInput(
                ServiceAccountServer,
                CertificateAuthorityData.Value,
                ServiceAccountToken)));

        using (prepared)
        {
            var persisted = Assert.IsType<ServiceAccountDeploymentTargetConnection>(prepared.Connection);
            Assert.Equal(ServiceAccountServer, persisted.ServerUrl);
            Assert.Equal(CertificateAuthorityData.Value, persisted.CertificateAuthorityData);
            Assert.NotEmpty(persisted.EncryptedCredential.Ciphertext);
            Assert.Equal(
                LocalKeyFileKeyEncryptionProvider.StableProviderId,
                persisted.EncryptedCredential.KeyProviderId);
            Assert.Equal(
                EnvelopeEncryptionService.CurrentFormatVersion,
                persisted.EncryptedCredential.FormatVersion);

            var persistedJson = persisted.ToBsonDocument().ToJson();
            Assert.DoesNotContain(ServiceAccountToken, persistedJson, StringComparison.Ordinal);
            Assert.DoesNotContain(ServiceAccountToken, persisted.ToString(), StringComparison.Ordinal);

            var dtoJson = JsonSerializer.Serialize(CreateTarget(targetId, persisted).ToDto());
            Assert.DoesNotContain(ServiceAccountToken, dtoJson, StringComparison.Ordinal);
            Assert.DoesNotContain(CertificateAuthorityData.Value, dtoJson, StringComparison.Ordinal);
            Assert.DoesNotContain(ServiceAccountServer, dtoJson, StringComparison.Ordinal);
            Assert.DoesNotContain("encrypted", dtoJson, StringComparison.OrdinalIgnoreCase);

            var resolved = AssertSuccess(await encryption.ConnectionService.CreateAsync(targetId, persisted));
            using (resolved)
            {
                AssertHardenedTransport(resolved, ServiceAccountServer);
            }
        }
    }

    [Fact]
    public async Task ServiceAccount_TransportTrustsOnlyEmbeddedCaAndExpectedHostname()
    {
        using var encryption = TestEncryptionScope.Create();
        using var certificates = TlsCertificateSet.Create("service-account.pole.test");
        var targetId = ObjectId.GenerateNewId().ToString();
        var prepared = AssertSuccess(await encryption.ConnectionService.PrepareAsync(
            targetId,
            connectionVersion: 1,
            new ServiceAccountConnectionInput(
                ServiceAccountServer,
                Convert.ToBase64String(certificates.TrustedRoot.Export(X509ContentType.Cert)),
                ServiceAccountToken)));

        using (prepared)
        {
            var handler = GetTransportHandler(prepared.Candidate);
            var validator = handler.SslOptions.RemoteCertificateValidationCallback;
            Assert.NotNull(validator);

            Assert.True(validator(
                handler,
                certificates.ValidLeaf,
                null,
                SslPolicyErrors.None));
            Assert.False(validator(
                handler,
                certificates.WrongHostLeaf,
                null,
                SslPolicyErrors.None));
            Assert.False(validator(
                handler,
                certificates.WrongAuthorityLeaf,
                null,
                SslPolicyErrors.None));
            Assert.False(validator(
                handler,
                certificates.ExpiredLeaf,
                null,
                SslPolicyErrors.None));
        }
    }

    [Theory]
    [InlineData("server")]
    [InlineData("ca")]
    public async Task ServiceAccount_Resolve_RejectsTamperedDeliveryMetadata(string metadata)
    {
        using var encryption = TestEncryptionScope.Create();
        var targetId = ObjectId.GenerateNewId().ToString();
        var prepared = AssertSuccess(await encryption.ConnectionService.PrepareAsync(
            targetId,
            connectionVersion: 1,
            new ServiceAccountConnectionInput(
                ServiceAccountServer,
                CertificateAuthorityData.Value,
                ServiceAccountToken)));

        using (prepared)
        {
            var persisted = Assert.IsType<ServiceAccountDeploymentTargetConnection>(prepared.Connection);
            var tampered = DeploymentTargetConnectionFactory.CreateServiceAccount(
                persisted.Version,
                metadata == "server" ? "https://attacker.pole.test:6443" : persisted.ServerUrl,
                metadata == "ca" ? AlternateCertificateAuthorityData.Value : persisted.CertificateAuthorityData,
                persisted.EncryptedCredential);

            var result = await encryption.ConnectionService.CreateAsync(targetId, tampered);

            var failure = AssertFailure(result);
            AssertSafeFailure(
                failure,
                ServiceAccountToken,
                Convert.ToBase64String(persisted.EncryptedCredential.Ciphertext),
                "attacker.pole.test");
        }
    }

    [Theory]
    [InlineData("target")]
    [InlineData("version")]
    public async Task Kubeconfig_Resolve_RejectsCrossOwnerOrVersionEnvelope(string changedIdentity)
    {
        using var encryption = TestEncryptionScope.Create();
        var targetId = ObjectId.GenerateNewId().ToString();
        var prepared = AssertSuccess(await encryption.ConnectionService.PrepareAsync(
            targetId,
            connectionVersion: 7,
            new KubeconfigConnectionInput(CreateKubeconfig(KubeconfigToken))));

        using (prepared)
        {
            var persisted = Assert.IsType<KubeconfigDeploymentTargetConnection>(prepared.Connection);
            var requestedTargetId = changedIdentity == "target"
                ? ObjectId.GenerateNewId().ToString()
                : targetId;
            DeploymentTargetConnection requestedConnection = changedIdentity == "version"
                ? DeploymentTargetConnectionFactory.CreateKubeconfig(
                    persisted.Version + 1,
                    persisted.ContextName,
                    persisted.ServerUrl,
                    persisted.EncryptedKubeconfig)
                : persisted;

            var result = await encryption.ConnectionService.CreateAsync(
                requestedTargetId,
                requestedConnection);

            var failure = AssertFailure(result);
            AssertSafeFailure(
                failure,
                KubeconfigToken,
                Convert.ToBase64String(persisted.EncryptedKubeconfig.Ciphertext),
                CertificateAuthorityData.Value);
        }
    }

    private static void AssertHardenedTransport(IKubernetes client, string expectedServer)
    {
        var kubernetes = Assert.IsType<Kubernetes>(client);
        Assert.Equal(new Uri(expectedServer), kubernetes.BaseUri);
        Assert.Equal(TimeSpan.FromSeconds(30), kubernetes.HttpClientTimeout);

        var handler = GetTransportHandler(client);
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    private static SocketsHttpHandler GetTransportHandler(IKubernetes client)
    {
        var kubernetes = Assert.IsType<Kubernetes>(client);
        var handlerField = typeof(Kubernetes).GetField(
            "<HttpClientHandler>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handlerField);
        return Assert.IsType<SocketsHttpHandler>(handlerField.GetValue(kubernetes));
    }

    private static string CreateUnsafeKubeconfig(string variant)
    {
        var server = variant == "http"
            ? "http://cluster.pole.test:6443"
            : KubernetesServer;
        var clusterSettings = variant switch
        {
            "insecure" => "    insecure-skip-tls-verify: true",
            "file" => "    certificate-authority: /pole/ca-file-must-not-be-read.pem",
            _ => string.Empty
        };
        var credentials = variant switch
        {
            "exec" => "    exec:\n      apiVersion: client.authentication.k8s.io/v1\n      command: pole-should-never-run",
            "basic" => "    username: pole-admin\n    password: pole-basic-password",
            "mixed" =>
                $"    token: {KubeconfigToken}\n    client-certificate-data: Y2VydGlmaWNhdGU=\n    client-key-data: cHJpdmF0ZS1rZXk=",
            _ => $"    token: {KubeconfigToken}"
        };
        return CreateKubeconfig(credentials, server, clusterSettings);
    }

    private static string CreateKubeconfig(string token) =>
        CreateKubeconfig($"    token: {token}", KubernetesServer, string.Empty);

    private static string CreateKubeconfig(
        string credentials,
        string server,
        string clusterSettings) =>
        $"""
        apiVersion: v1
        kind: Config
        current-context: pole-context
        clusters:
        - name: pole-cluster
          cluster:
            server: {server}
            certificate-authority-data: {CertificateAuthorityData.Value}
        {clusterSettings}
        contexts:
        - name: pole-context
          context:
            cluster: pole-cluster
            user: pole-user
        users:
        - name: pole-user
          user:
        {credentials}
        """;

    private static DeploymentTarget CreateTarget(
        string id,
        DeploymentTargetConnection connection)
    {
        var createdAtUtc = DateTime.UtcNow;
        return DeploymentTargetFactory.Create(
            "Security test target",
            $"cluster-{id}",
            connection,
            DeploymentTargetHealth.Unknown(connection.Version),
            DeploymentTargetCapabilities.Unknown(connection.Version),
            enabled: true,
            acceptsWorkloads: true,
            new DeploymentTargetFactory.CreateOptions(id, createdAtUtc));
    }

    private static T AssertSuccess<T>(Result<T> result) where T : notnull =>
        Assert.IsType<Success<T>>(result).Value;

    private static Failure AssertFailure<T>(Result<T> result) where T : notnull =>
        Assert.IsType<Failure<T>>(result).Error;

    private static void AssertSafeFailure(Failure failure, params string[] sensitiveMarkers)
    {
        var rendered = RenderFailure(failure);
        foreach (var marker in sensitiveMarkers)
            Assert.DoesNotContain(marker, rendered, StringComparison.Ordinal);
    }

    private static string RenderFailure(Failure failure)
    {
        var values = new List<string> { failure.Message };
        switch (failure)
        {
            case ValidationFailure validation:
                values.AddRange(validation.ValidationErrors.Keys);
                values.AddRange(validation.ValidationErrors.Values.SelectMany(messages => messages));
                break;
            case InternalFailure internalFailure:
                values.Add(internalFailure.Details);
                break;
            case ExternalServiceFailure externalService:
                values.Add(externalService.ServiceName);
                values.Add(externalService.Details);
                break;
            case UnauthorizedFailure unauthorized:
                values.Add(unauthorized.Action);
                break;
            case ForbiddenFailure forbidden:
                values.Add(forbidden.Action);
                break;
            case NotFoundFailure notFound when notFound.Id is not null:
                values.Add(notFound.Id.ToString() ?? string.Empty);
                break;
        }

        foreach (var inner in failure.InnerFailures)
            values.Add(RenderFailure(inner));
        return string.Join(Environment.NewLine, values);
    }

    private static string CreateCertificateAuthorityData(string commonName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(certificate.ExportCertificatePem()));
    }

    private sealed class TestEncryptionScope : IDisposable
    {
        private readonly string _directory;
        private readonly LocalKeyFileKeyEncryptionProvider _provider;

        private TestEncryptionScope(
            string directory,
            LocalKeyFileKeyEncryptionProvider provider,
            DeploymentTargetConnectionService connectionService)
        {
            _directory = directory;
            _provider = provider;
            ConnectionService = connectionService;
        }

        public DeploymentTargetConnectionService ConnectionService { get; }

        public static TestEncryptionScope Create()
        {
            var parent = Path.Combine(Path.GetTempPath(), "pole-deployment-target-connection-tests");
            var directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var keyPath = Path.Combine(directory, "local-key-v1.b64");
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllText(keyPath, Convert.ToBase64String(key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            var provider = new LocalKeyFileKeyEncryptionProvider(Options.Create(new LocalKeyFileOptions
            {
                ActiveKeyId = "deployment-target-test",
                ActiveKeyVersion = 1,
                Keys =
                [
                    new LocalKeyFileKeyOptions
                    {
                        Id = "deployment-target-test",
                        Version = 1,
                        Path = keyPath
                    }
                ]
            }));
            var envelopeEncryption = new EnvelopeEncryptionService(
                [provider],
                Options.Create(new EnvelopeEncryptionOptions
                {
                    ActiveProviderId = LocalKeyFileKeyEncryptionProvider.StableProviderId
                }));
            var protector = new DeploymentTargetConnectionProtector(envelopeEncryption);
            return new TestEncryptionScope(
                directory,
                provider,
                new DeploymentTargetConnectionService(protector));
        }

        public void Dispose()
        {
            _provider.Dispose();
            var fullDirectory = Path.GetFullPath(_directory);
            var expectedParent = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "pole-deployment-target-connection-tests"));
            if (!fullDirectory.StartsWith(
                    expectedParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a deployment-target test directory outside the expected temporary root.");
            }

            if (Directory.Exists(fullDirectory))
                Directory.Delete(fullDirectory, recursive: true);
        }
    }

    private sealed class TlsCertificateSet : IDisposable
    {
        private readonly X509Certificate2[] _certificates;

        private TlsCertificateSet(
            X509Certificate2 trustedRoot,
            X509Certificate2 validLeaf,
            X509Certificate2 wrongHostLeaf,
            X509Certificate2 wrongAuthorityLeaf,
            X509Certificate2 expiredLeaf,
            X509Certificate2[] certificates)
        {
            TrustedRoot = trustedRoot;
            ValidLeaf = validLeaf;
            WrongHostLeaf = wrongHostLeaf;
            WrongAuthorityLeaf = wrongAuthorityLeaf;
            ExpiredLeaf = expiredLeaf;
            _certificates = certificates;
        }

        public X509Certificate2 TrustedRoot { get; }
        public X509Certificate2 ValidLeaf { get; }
        public X509Certificate2 WrongHostLeaf { get; }
        public X509Certificate2 WrongAuthorityLeaf { get; }
        public X509Certificate2 ExpiredLeaf { get; }

        public static TlsCertificateSet Create(string expectedHost)
        {
            var trustedRoot = CreateRoot("pole-trusted-root");
            var wrongRoot = CreateRoot("pole-wrong-root");
            var now = DateTimeOffset.UtcNow;
            var validLeaf = CreateLeaf(
                trustedRoot,
                expectedHost,
                now.AddMinutes(-5),
                now.AddHours(1));
            var wrongHostLeaf = CreateLeaf(
                trustedRoot,
                "attacker.pole.test",
                now.AddMinutes(-5),
                now.AddHours(1));
            var wrongAuthorityLeaf = CreateLeaf(
                wrongRoot,
                expectedHost,
                now.AddMinutes(-5),
                now.AddHours(1));
            var expiredLeaf = CreateLeaf(
                trustedRoot,
                expectedHost,
                now.AddHours(-2),
                now.AddHours(-1));
            return new TlsCertificateSet(
                trustedRoot,
                validLeaf,
                wrongHostLeaf,
                wrongAuthorityLeaf,
                expiredLeaf,
                [trustedRoot, wrongRoot, validLeaf, wrongHostLeaf, wrongAuthorityLeaf, expiredLeaf]);
        }

        public void Dispose()
        {
            foreach (var certificate in _certificates)
                certificate.Dispose();
        }

        private static X509Certificate2 CreateRoot(string commonName)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={commonName}",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-2),
                DateTimeOffset.UtcNow.AddDays(2));
        }

        private static X509Certificate2 CreateLeaf(
            X509Certificate2 issuer,
            string dnsName,
            DateTimeOffset notBefore,
            DateTimeOffset notAfter)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={dnsName}",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                true));
            var subjectAlternativeName = new SubjectAlternativeNameBuilder();
            subjectAlternativeName.AddDnsName(dnsName);
            request.CertificateExtensions.Add(subjectAlternativeName.Build());
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request.Create(
                issuer,
                notBefore,
                notAfter,
                RandomNumberGenerator.GetBytes(16));
        }
    }
}
