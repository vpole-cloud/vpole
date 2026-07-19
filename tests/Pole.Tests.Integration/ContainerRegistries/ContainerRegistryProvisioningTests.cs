using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Encryption;
using Pole.Features.ContainerRegistries.Application.Migrations;
using Pole.Features.ContainerRegistries.Application.Endpoints;
using Pole.Features.ContainerRegistries.Application.Provisioning;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ContainerRegistries;

public sealed class ContainerRegistryProvisioningTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Provisioning_PersistsOnlyProtectedCredentialsAndIsIdempotent()
    {
        await new ContainerRegistryMigration().Up(Fixture.Database);
        var registryClient = new FakeContainerRegistryClient();
        using var encryption = TestEnvelopeEncryptionScope.Create();
        var protector = new RegistryCredentialProtector(encryption.Service);
        var service = new ContainerRegistryProvisioningService(
            Fixture.DbContext,
            registryClient,
            protector,
            TimeProvider.System);

        var first = await service.EnsureTenantProjectAsync(TestData.TenantId, TestData.UserId);
        var second = await service.EnsureTenantProjectAsync(TestData.TenantId, TestData.UserId);

        Assert.True(first.TryGetValue(out var project, out _));
        Assert.True(second.TryGetValue(out var sameProject, out _));
        Assert.Equal(project.Id, sameProject.Id);
        Assert.Equal(1, registryClient.ProvisionCalls);
        var ready = Assert.IsType<ReadyRegistryProject>(project.State);

        var credentials = await Fixture.DbContext.RegistryCredentials
            .Find(item => item.RegistryProjectId == project.Id)
            .ToListAsync();
        Assert.Equal(2, credentials.Count);
        Assert.All(credentials, credential =>
        {
            Assert.Equal(LocalKeyFileKeyEncryptionProvider.StableProviderId, credential.EncryptedSecret.KeyProviderId);
            Assert.Equal("integration", credential.EncryptedSecret.KeyEncryptionKeyId);
            Assert.Equal(1, credential.EncryptedSecret.KeyEncryptionKeyVersion);
            Assert.Equal(EnvelopeEncryptionService.CurrentFormatVersion, credential.EncryptedSecret.FormatVersion);
        });

        var rawCredentials = await Fixture.Database
            .GetCollection<BsonDocument>("RegistryCredentials")
            .Find(Builders<BsonDocument>.Filter.Eq("RegistryProjectId", project.Id))
            .ToListAsync();
        Assert.Equal(2, rawCredentials.Count);
        Assert.All(rawCredentials, AssertRawCredentialDoesNotContainPlaintext);

        var resolver = new RegistryCredentialResolver(Fixture.DbContext, protector, TimeProvider.System);
        var resolved = await resolver.ResolveAsync(
            TestData.TenantId,
            project.Id,
            RegistryCredentialPurpose.BuildPushPull);
        Assert.True(resolved.TryGetValue(out var buildCredential, out _));
        Assert.Equal(FakeContainerRegistryClient.BuildSecret, buildCredential.Secret);
        Assert.Equal(ready.BuildCredentialId, credentials.Single(item =>
            item.Purpose == RegistryCredentialPurpose.BuildPushPull).Id);
    }

    [Fact]
    public async Task FailedProvisioning_UsesExplicitStateAndCanBeRetried()
    {
        var registryClient = new FakeContainerRegistryClient
        {
            FailNextProvision = true
        };
        using var encryption = TestEnvelopeEncryptionScope.Create();
        var protector = new RegistryCredentialProtector(encryption.Service);
        var service = new ContainerRegistryProvisioningService(
            Fixture.DbContext,
            registryClient,
            protector,
            TimeProvider.System);

        var failed = await service.EnsureTenantProjectAsync(TestData.TenantId, TestData.UserId);

        Assert.False(failed.TryGetValue(out _, out _));
        var storedFailure = await Fixture.DbContext.RegistryProjects
            .Find(item => item.TenantId == TestData.TenantId)
            .SingleAsync();
        Assert.IsType<FailedRegistryProjectProvisioning>(storedFailure.State);

        var retried = await service.EnsureTenantProjectAsync(TestData.TenantId, TestData.UserId);

        Assert.True(retried.TryGetValue(out var ready, out _));
        Assert.IsType<ReadyRegistryProject>(ready.State);
        Assert.Equal(2, registryClient.ProvisionCalls);
    }

    [Fact]
    public async Task CredentialProtector_ReturnsFailureForTamperedPayloadAndTenantSubstitution()
    {
        using var encryption = TestEnvelopeEncryptionScope.Create();
        var protector = new RegistryCredentialProtector(encryption.Service);
        var credentialId = ObjectId.GenerateNewId().ToString();
        var protectedResult = await protector.ProtectAsync(TestData.TenantId, credentialId, 1, "secret");
        Assert.True(protectedResult.TryGetValue(out var protectedSecret, out _));
        var credential = RegistryCredentialFactory.Create(
            TestData.TenantId,
            ObjectId.GenerateNewId().ToString(),
            RegistryCredentialPurpose.BuildPushPull,
            "robot-id",
            "robot-user",
            1,
            protectedSecret,
            null,
            new RegistryCredentialFactory.CreateOptions(credentialId, DateTime.UtcNow));

        var tamperedTag = protectedSecret.AuthenticationTag.ToArray();
        tamperedTag[0] ^= 0x01;
        var tampered = credential with
        {
            EncryptedSecret = protectedSecret with { AuthenticationTag = tamperedTag }
        };

        var tamperedResult = await protector.UnprotectAsync(tampered);
        var substitutedTenantResult = await protector.UnprotectAsync(credential with { TenantId = "other-tenant" });

        Assert.False(tamperedResult.TryGetValue(out _, out var tamperedFailure));
        Assert.IsType<InternalFailure>(tamperedFailure);
        Assert.False(substitutedTenantResult.TryGetValue(out _, out var substitutionFailure));
        Assert.IsType<InternalFailure>(substitutionFailure);
        Assert.DoesNotContain("secret", tamperedFailure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", credential.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialRewrap_ChangesOnlyWrappedKeyAndIsIdempotent()
    {
        await new ContainerRegistryMigration().Up(Fixture.Database);
        using var encryption = RotationEncryptionScope.Create();
        var credentialId = ObjectId.GenerateNewId().ToString();
        var oldProtector = new RegistryCredentialProtector(encryption.OldService);
        var encryptedResult = await oldProtector.ProtectAsync(
            TestData.TenantId,
            credentialId,
            1,
            "rotation-secret");
        Assert.True(encryptedResult.TryGetValue(out var oldEnvelope, out _));
        var credential = RegistryCredentialFactory.Create(
            TestData.TenantId,
            ObjectId.GenerateNewId().ToString(),
            RegistryCredentialPurpose.BuildPushPull,
            "robot-id",
            "robot-user",
            1,
            oldEnvelope,
            null,
            new RegistryCredentialFactory.CreateOptions(credentialId, DateTime.UtcNow));
        await Fixture.DbContext.RegistryCredentials.InsertOneAsync(credential);

        var service = new RegistryCredentialRewrapService(Fixture.DbContext, encryption.RotatingService);
        var wrongTarget = encryption.RotatingService.ActiveKey with { KeyVersion = 99 };
        var rejected = await service.RewrapAllAsync(wrongTarget);
        Assert.False(rejected.TryGetValue(out _, out var rejectedFailure));
        Assert.IsType<ConflictFailure>(rejectedFailure);

        var first = await service.RewrapAllAsync(encryption.RotatingService.ActiveKey);
        Assert.True(first.TryGetValue(out var firstSummary, out _));
        Assert.Equal(1, firstSummary.Examined);
        Assert.Equal(1, firstSummary.Rewrapped);
        Assert.Equal(0, firstSummary.ConcurrentChanges);

        var rewrapped = await Fixture.DbContext.RegistryCredentials
            .Find(item => item.Id == credentialId)
            .SingleAsync();
        Assert.Equal("new", rewrapped.EncryptedSecret.KeyEncryptionKeyId);
        Assert.Equal(2, rewrapped.EncryptedSecret.KeyEncryptionKeyVersion);
        Assert.True(oldEnvelope.Ciphertext.SequenceEqual(rewrapped.EncryptedSecret.Ciphertext));
        Assert.True(oldEnvelope.Nonce.SequenceEqual(rewrapped.EncryptedSecret.Nonce));
        Assert.True(oldEnvelope.AuthenticationTag.SequenceEqual(rewrapped.EncryptedSecret.AuthenticationTag));

        var newOnlyResult = await new RegistryCredentialProtector(encryption.NewOnlyService)
            .UnprotectAsync(rewrapped);
        Assert.True(newOnlyResult.TryGetValue(out var plaintext, out _));
        Assert.Equal("rotation-secret", plaintext);
        var oldOnlyResult = await oldProtector.UnprotectAsync(rewrapped);
        Assert.False(oldOnlyResult.TryGetValue(out _, out _));

        var second = await service.RewrapAllAsync(encryption.RotatingService.ActiveKey);
        Assert.True(second.TryGetValue(out var secondSummary, out _));
        Assert.Equal(1, secondSummary.Examined);
        Assert.Equal(0, secondSummary.Rewrapped);
        Assert.Equal(0, secondSummary.ConcurrentChanges);
    }

    [Fact]
    public async Task ProvisioningCancellationAfterRemoteCreationCompensatesAndMarksFailed()
    {
        await new ContainerRegistryMigration().Up(Fixture.Database);
        var registryClient = new FakeContainerRegistryClient();
        var service = new ContainerRegistryProvisioningService(
            Fixture.DbContext,
            registryClient,
            new CancellingCredentialProtector(),
            TimeProvider.System);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.EnsureTenantProjectAsync(TestData.TenantId, TestData.UserId));

        Assert.Equal(1, registryClient.DeleteCalls);
        var project = await Fixture.DbContext.RegistryProjects
            .Find(item => item.TenantId == TestData.TenantId)
            .SingleAsync();
        Assert.IsType<FailedRegistryProjectProvisioning>(project.State);
    }

    [Fact]
    public async Task CredentialRewrapEndpointRequiresPlatformAdminAndExactActiveKey()
    {
        var body = new RewrapRegistryCredentialsRequestBody(
            LocalKeyFileKeyEncryptionProvider.StableProviderId,
            "api-integration",
            1,
            LocalKeyFileKeyEncryptionProvider.Algorithm,
            LocalKeyFileKeyEncryptionProvider.AlgorithmVersion);

        var forbidden = await Client.PostAsJsonAsync(
            "/api/v1/platform/encryption/container-registry-credentials/rewrap",
            body);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var accepted = await Client.PostAsJsonAsync(
            "/api/v1/platform/encryption/container-registry-credentials/rewrap",
            body);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var summary = await accepted.Content.ReadFromJsonAsync<RegistryCredentialRewrapSummary>();
        Assert.NotNull(summary);
        Assert.Equal(0, summary.Examined);
    }

    private static void AssertRawCredentialDoesNotContainPlaintext(BsonDocument credential)
    {
        var rawBson = credential.ToBson().AsSpan();
        foreach (var plaintext in new[]
                 {
                     FakeContainerRegistryClient.BuildSecret,
                     FakeContainerRegistryClient.PullSecret
                 })
        {
            var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
            var encodedPlaintextBytes = System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintextBytes));
            Assert.True(rawBson.IndexOf(plaintextBytes) < 0);
            Assert.True(rawBson.IndexOf(encodedPlaintextBytes) < 0);
        }
    }

    private sealed class TestEnvelopeEncryptionScope : IDisposable
    {
        private readonly string _directory;
        private readonly LocalKeyFileKeyEncryptionProvider _provider;

        private TestEnvelopeEncryptionScope(
            string directory,
            LocalKeyFileKeyEncryptionProvider provider,
            IEnvelopeEncryptionService service)
        {
            _directory = directory;
            _provider = provider;
            Service = service;
        }

        public IEnvelopeEncryptionService Service { get; }

        public static TestEnvelopeEncryptionScope Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "pole-encryption-integration", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "integration-v1.key");
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllText(path, Convert.ToBase64String(key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            var provider = new LocalKeyFileKeyEncryptionProvider(Options.Create(new LocalKeyFileOptions
            {
                ActiveKeyId = "integration",
                ActiveKeyVersion = 1,
                Keys =
                [
                    new LocalKeyFileKeyOptions { Id = "integration", Version = 1, Path = path }
                ]
            }));
            var service = new EnvelopeEncryptionService(
                [provider],
                Options.Create(new EnvelopeEncryptionOptions
                {
                    ActiveProviderId = LocalKeyFileKeyEncryptionProvider.StableProviderId
                }));
            return new TestEnvelopeEncryptionScope(directory, provider, service);
        }

        public void Dispose()
        {
            _provider.Dispose();
            Directory.Delete(_directory, recursive: true);
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
            var directory = Path.Combine(
                Path.GetTempPath(),
                "pole-encryption-rotation-integration",
                Guid.NewGuid().ToString("N"));
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
            Directory.Delete(_directory, recursive: true);
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

        private static IEnvelopeEncryptionService CreateService(LocalKeyFileKeyEncryptionProvider provider) =>
            new EnvelopeEncryptionService(
                [provider],
                Options.Create(new EnvelopeEncryptionOptions
                {
                    ActiveProviderId = LocalKeyFileKeyEncryptionProvider.StableProviderId
                }));
    }

    private sealed class CancellingCredentialProtector : IRegistryCredentialProtector
    {
        public ValueTask<Result<EncryptedValue>> ProtectAsync(
            string tenantId,
            string credentialId,
            long secretVersion,
            string secret,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<Result<EncryptedValue>>(new OperationCanceledException());

        public ValueTask<Result<string>> UnprotectAsync(
            RegistryCredential credential,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeContainerRegistryClient : IContainerRegistryClient
    {
        public const string BuildSecret = "raw-build-secret";
        public const string PullSecret = "raw-pull-secret";

        public int ProvisionCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public bool FailNextProvision { get; init; }

        public Task<Result<ProvisionedRegistryProject>> ProvisionProjectAsync(
            RegistryProjectProvisioningRequest request,
            CancellationToken cancellationToken = default)
        {
            ProvisionCalls++;
            if (FailNextProvision && ProvisionCalls == 1)
            {
                return Task.FromResult<Result<ProvisionedRegistryProject>>(
                    Failures.ExternalService("Harbor provisioning failed.", "Harbor", "Status 500."));
            }

            return Task.FromResult<Result<ProvisionedRegistryProject>>(new ProvisionedRegistryProject(
                "17",
                request.ProjectName,
                new RegistryRobotCredential("21", "robot$build", BuildSecret, null),
                new RegistryRobotCredential("22", "robot$runtime", PullSecret, null)));
        }

        public Task<Result<RegistryArtifact>> GetArtifactAsync(
            string projectName,
            string repositoryName,
            string reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<RegistryArtifact>>(Failures.NotFound("Artifact not found."));

        public Task<Result> DeleteProjectAsync(
            string externalProjectId,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.FromResult<Result>(Result.Success());
        }
    }
}
