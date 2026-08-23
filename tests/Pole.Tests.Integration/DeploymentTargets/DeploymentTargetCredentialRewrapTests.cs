using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using k8s;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Security;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using static Pole.Tests.Integration.Infrastructure.ResultAssertions;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

[Collection(IntegrationTestCollection.Name)]
public sealed class DeploymentTargetCredentialRewrapTests(MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private const string KubeconfigToken = "rewrap-kubeconfig-token";
    private const string ServiceAccountToken = "rewrap-service-account-token";
    private const string KubernetesServer = "https://rewrap.pole.test:6443";

    private static readonly Lazy<string> CertificateAuthorityData =
        new(CreateCertificateAuthorityData);

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RewrapAllAsync_RewrapsEveryCredentialKind_AndIsIdempotent()
    {
        using var encryption = RotationEncryptionScope.Create();
        var oldConnectionService = CreateConnectionService(encryption.OldService);
        var kubeconfigTargetId = ObjectId.GenerateNewId().ToString();
        var serviceAccountTargetId = ObjectId.GenerateNewId().ToString();

        using var preparedKubeconfig = AssertSuccess(await oldConnectionService.PrepareAsync(
            kubeconfigTargetId,
            connectionVersion: 1,
            new KubeconfigConnectionInput(CreateKubeconfig())));
        using var preparedServiceAccount = AssertSuccess(await oldConnectionService.PrepareAsync(
            serviceAccountTargetId,
            connectionVersion: 1,
            new ServiceAccountConnectionInput(
                KubernetesServer,
                CertificateAuthorityData.Value,
                ServiceAccountToken)));

        var kubeconfigConnection = Assert.IsType<KubeconfigDeploymentTargetConnection>(
            preparedKubeconfig.Connection);
        var serviceAccountConnection = Assert.IsType<ServiceAccountDeploymentTargetConnection>(
            preparedServiceAccount.Connection);
        var inClusterTarget = CreateTarget(
            ObjectId.GenerateNewId().ToString(),
            DeploymentTargetConnectionFactory.CreateInCluster(1));
        await fixture.DbContext.DeploymentTargets.InsertManyAsync(
        [
            CreateTarget(kubeconfigTargetId, kubeconfigConnection),
            CreateTarget(serviceAccountTargetId, serviceAccountConnection),
            inClusterTarget
        ]);

        var service = new MongoRewrapDeploymentTargetCredentials(
            fixture.DbContext.DeploymentTargets,
            encryption.RotatingService);
        var wrongKey = encryption.RotatingService.ActiveKey with { KeyVersion = 99 };

        var rejected = await service.InvokeAsync(wrongKey);
        Assert.IsType<DeploymentTargetCredentialRewrapConflict>(
            AssertFailure(rejected));

        var first = AssertSuccess(await service.InvokeAsync(
            encryption.RotatingService.ActiveKey));

        Assert.Equal(2, first.Examined);
        Assert.Equal(2, first.Rewrapped);
        Assert.Equal(0, first.ConcurrentChanges);

        var stored = await fixture.DbContext.DeploymentTargets
            .Find(target => target.Id == kubeconfigTargetId || target.Id == serviceAccountTargetId)
            .ToListAsync();
        var storedKubeconfig = Assert.IsType<KubeconfigDeploymentTargetConnection>(
            stored.Single(target => target.Id == kubeconfigTargetId).Connection);
        var storedServiceAccount = Assert.IsType<ServiceAccountDeploymentTargetConnection>(
            stored.Single(target => target.Id == serviceAccountTargetId).Connection);
        AssertRewrapped(kubeconfigConnection.EncryptedKubeconfig, storedKubeconfig.EncryptedKubeconfig);
        AssertRewrapped(serviceAccountConnection.EncryptedCredential, storedServiceAccount.EncryptedCredential);

        var newOnlyConnectionService = CreateConnectionService(encryption.NewOnlyService);
        using var resolvedKubeconfig = AssertSuccess(await newOnlyConnectionService.CreateAsync(
            kubeconfigTargetId,
            storedKubeconfig));
        using var resolvedServiceAccount = AssertSuccess(await newOnlyConnectionService.CreateAsync(
            serviceAccountTargetId,
            storedServiceAccount));

        AssertFailure(await oldConnectionService.CreateAsync(kubeconfigTargetId, storedKubeconfig));
        AssertFailure(await oldConnectionService.CreateAsync(serviceAccountTargetId, storedServiceAccount));

        var second = AssertSuccess(await service.InvokeAsync(
            encryption.RotatingService.ActiveKey));
        Assert.Equal(2, second.Examined);
        Assert.Equal(0, second.Rewrapped);
        Assert.Equal(0, second.ConcurrentChanges);
    }

    [Fact]
    public async Task MetadataUpdate_DoesNotRestoreEnvelopeReadBeforeConcurrentRewrap()
    {
        using var encryption = RotationEncryptionScope.Create();
        var oldConnectionService = CreateConnectionService(encryption.OldService);
        var targetId = ObjectId.GenerateNewId().ToString();
        using var prepared = AssertSuccess(await oldConnectionService.PrepareAsync(
            targetId,
            connectionVersion: 1,
            new ServiceAccountConnectionInput(
                KubernetesServer,
                CertificateAuthorityData.Value,
                ServiceAccountToken)));
        var original = CreateTarget(targetId, prepared.Connection);
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(original);
        var rewrapService = new MongoRewrapDeploymentTargetCredentials(
            fixture.DbContext.DeploymentTargets,
            encryption.RotatingService);
        var timeProvider = new CallbackTimeProvider(
            DateTimeOffset.UtcNow.AddMinutes(1),
            () =>
            {
                var summary = AssertSuccess(rewrapService.InvokeAsync(
                        encryption.RotatingService.ActiveKey)
                    .GetAwaiter()
                    .GetResult());
                Assert.Equal(1, summary.Rewrapped);
            });
        var handler = new UpdateDeploymentTargetHandler(
            LoadById,
            LoadDefaultId,
            new MongoPersistUpdatedDeploymentTarget(
                fixture.DbContext.DeploymentTargets,
                CreateDomainAdmission(),
                new MongoRunTransaction(fixture.Client).InvokeOutcomeAsync<Result<
                    DeploymentTarget,
                    PersistUpdatedDeploymentTargetError>>).InvokeAsync,
            new NoOpClientCache().Invalidate,
            timeProvider);

        var result = await handler.HandleAsync(
            new UpdateDeploymentTargetRequest(
                targetId,
                new UpdateDeploymentTargetRequestBody(
                    "Updated while rewrapping",
                    Enabled: true,
                    AcceptsWorkloads: true,
                    original.WorkloadConfiguration.ToRequestBody(),
                    original.Revision)));

        var response = result switch
        {
            DeploymentTargetDto value => value,
            _ => throw new Xunit.Sdk.XunitException("Expected a successful update outcome.")
        };
        Assert.Equal("Updated while rewrapping", response.DisplayName);
        var stored = await fixture.DbContext.DeploymentTargets
            .Find(target => target.Id == targetId)
            .SingleAsync();
        var storedConnection = Assert.IsType<ServiceAccountDeploymentTargetConnection>(
            stored.Connection);
        Assert.Equal("new", storedConnection.EncryptedCredential.KeyEncryptionKeyId);
        Assert.Equal(2, storedConnection.EncryptedCredential.KeyEncryptionKeyVersion);
        using var resolved = AssertSuccess(await CreateConnectionService(encryption.NewOnlyService)
            .CreateAsync(targetId, storedConnection));
    }

    private static void AssertRewrapped(EncryptedValue oldEnvelope, EncryptedValue newEnvelope)
    {
        Assert.Equal("new", newEnvelope.KeyEncryptionKeyId);
        Assert.Equal(2, newEnvelope.KeyEncryptionKeyVersion);
        Assert.True(oldEnvelope.Ciphertext.SequenceEqual(newEnvelope.Ciphertext));
        Assert.True(oldEnvelope.Nonce.SequenceEqual(newEnvelope.Nonce));
        Assert.True(oldEnvelope.AuthenticationTag.SequenceEqual(newEnvelope.AuthenticationTag));
    }

    private static DeploymentTargetConnectionOperations CreateConnectionService(
        IEnvelopeEncryptionService encryptionService)
    {
        var protector = new DeploymentTargetConnectionProtector(encryptionService);
        return new DeploymentTargetConnectionOperations(
            new DeploymentTargetConnectionPreparer(protector).InvokeAsync,
            new DeploymentTargetKubernetesClientFactory(protector).InvokeAsync);
    }

    private sealed class DeploymentTargetConnectionOperations(
        PrepareDeploymentTargetConnection prepareDeploymentTargetConnection,
        CreateDeploymentTargetKubernetesClient createDeploymentTargetKubernetesClient)
    {
        public ValueTask<Result<
            PreparedDeploymentTargetConnection,
            DeploymentTargetConnectionRejected>> PrepareAsync(
            string deploymentTargetId,
            long connectionVersion,
            DeploymentTargetConnectionInput input,
            CancellationToken cancellationToken = default) =>
            prepareDeploymentTargetConnection(
                deploymentTargetId,
                connectionVersion,
                input,
                cancellationToken);

        public ValueTask<Result<
            IKubernetes,
            StoredDeploymentTargetConnectionUnavailable>> CreateAsync(
            string deploymentTargetId,
            DeploymentTargetConnection connection,
            CancellationToken cancellationToken = default) =>
            createDeploymentTargetKubernetesClient(
                deploymentTargetId,
                connection,
                cancellationToken);
    }

    private static DeploymentTarget CreateTarget(
        string id,
        DeploymentTargetConnection connection)
    {
        var now = DateTime.UtcNow;
        return DeploymentTargetFactory.Create(
            $"Rewrap target {id}",
            $"cluster-{id}",
            connection,
            DeploymentTargetHealth.Unknown(connection.Version),
            DeploymentTargetCapabilities.Unknown(connection.Version),
            DeploymentTargetTestData.CreateWorkloadConfiguration(),
            enabled: true,
            acceptsWorkloads: true,
            new DeploymentTargetFactory.CreateOptions(id, now));
    }

    private static string CreateKubeconfig() =>
        $"""
        apiVersion: v1
        kind: Config
        current-context: pole-context
        clusters:
        - name: pole-cluster
          cluster:
            server: {KubernetesServer}
            certificate-authority-data: {CertificateAuthorityData.Value}
        contexts:
        - name: pole-context
          context:
            cluster: pole-cluster
            user: pole-user
        users:
        - name: pole-user
          user:
            token: {KubeconfigToken}
        """;

    private static string CreateCertificateAuthorityData()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=pole-rewrap-test-ca",
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

    private DeploymentTargetDomainAdmission CreateDomainAdmission() =>
        new(
            fixture.DbContext.DeploymentTargets,
            fixture.DbContext.DeploymentTargetReferences,
            fixture.Database.GetCollection<DeploymentTargetPublicBaseDomainAdmissionFence>(
                DeploymentTargetPublicBaseDomainAdmissionFence.CollectionName));

    private Task<DeploymentTarget?> LoadById(
        string id,
        CancellationToken cancellationToken) =>
        LoadTargetAsync(id, cancellationToken);

    private async Task<DeploymentTarget?> LoadTargetAsync(
        string id,
        CancellationToken cancellationToken) =>
        await fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<string?> LoadDefaultId(CancellationToken cancellationToken) =>
        await fixture.DbContext.DeploymentTargetSettings
            .Find(item => item.Id == DeploymentTargetSettings.SingletonId)
            .Project(item => item.DefaultDeploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);

    private sealed class NoOpClientCache
    {
        public void Invalidate(string deploymentTargetId)
        {
        }
    }

    private sealed class CallbackTimeProvider(
        DateTimeOffset utcNow,
        Action callback) : TimeProvider
    {
        private int _called;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _called, 1) == 0)
                callback();
            return utcNow;
        }
    }

    private sealed class RotationEncryptionScope : IDisposable
    {
        private readonly string _directory;
        private readonly LocalKeyFileKeyEncryptionProvider[] _providers;

        private RotationEncryptionScope(
            string directory,
            LocalKeyFileKeyEncryptionProvider[] providers,
            IEnvelopeEncryptionService oldService,
            IEnvelopeEncryptionService rotatingService,
            IEnvelopeEncryptionService newOnlyService)
        {
            _directory = directory;
            _providers = providers;
            OldService = oldService;
            RotatingService = rotatingService;
            NewOnlyService = newOnlyService;
        }

        public IEnvelopeEncryptionService OldService { get; }
        public IEnvelopeEncryptionService RotatingService { get; }
        public IEnvelopeEncryptionService NewOnlyService { get; }

        public static RotationEncryptionScope Create()
        {
            var parent = Path.Combine(Path.GetTempPath(), "pole-deployment-target-rewrap-tests");
            var directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var oldPath = WriteKey(directory, "old-v1.key");
            var newPath = WriteKey(directory, "new-v2.key");
            var oldProvider = CreateProvider("old", 1, ("old", 1, oldPath));
            var rotatingProvider = CreateProvider(
                "new",
                2,
                ("old", 1, oldPath),
                ("new", 2, newPath));
            var newOnlyProvider = CreateProvider("new", 2, ("new", 2, newPath));
            return new RotationEncryptionScope(
                directory,
                [oldProvider, rotatingProvider, newOnlyProvider],
                CreateService(oldProvider),
                CreateService(rotatingProvider),
                CreateService(newOnlyProvider));
        }

        public void Dispose()
        {
            foreach (var provider in _providers)
                provider.Dispose();

            var fullDirectory = Path.GetFullPath(_directory);
            var expectedParent = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "pole-deployment-target-rewrap-tests"));
            if (!fullDirectory.StartsWith(
                    expectedParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a rewrap test directory outside the expected temporary root.");
            }

            if (Directory.Exists(fullDirectory))
                Directory.Delete(fullDirectory, recursive: true);
        }

        private static string WriteKey(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllText(path, Convert.ToBase64String(key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            return path;
        }

        private static LocalKeyFileKeyEncryptionProvider CreateProvider(
            string activeId,
            int activeVersion,
            params (string Id, int Version, string Path)[] keys) =>
            new(Options.Create(new LocalKeyFileOptions
            {
                ActiveKeyId = activeId,
                ActiveKeyVersion = activeVersion,
                Keys = keys.Select(key => new LocalKeyFileKeyOptions
                {
                    Id = key.Id,
                    Version = key.Version,
                    Path = key.Path
                }).ToList()
            }));

        private static IEnvelopeEncryptionService CreateService(
            LocalKeyFileKeyEncryptionProvider provider) =>
            new EnvelopeEncryptionService(
                [provider],
                Options.Create(new EnvelopeEncryptionOptions
                {
                    ActiveProviderId = LocalKeyFileKeyEncryptionProvider.StableProviderId
                }));
    }
}
