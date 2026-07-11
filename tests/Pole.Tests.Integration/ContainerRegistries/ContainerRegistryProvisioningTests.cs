using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Migrations;
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
        var protector = new RegistryCredentialProtector(new EphemeralDataProtectionProvider());
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
            Assert.NotEqual(FakeContainerRegistryClient.BuildSecret, credential.ProtectedSecret);
            Assert.NotEqual(FakeContainerRegistryClient.PullSecret, credential.ProtectedSecret);
            var bson = credential.ToBson().AsSpan();
            Assert.False(bson.IndexOf(System.Text.Encoding.UTF8.GetBytes(FakeContainerRegistryClient.BuildSecret)) >= 0);
            Assert.False(bson.IndexOf(System.Text.Encoding.UTF8.GetBytes(FakeContainerRegistryClient.PullSecret)) >= 0);
        });

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
        var protector = new RegistryCredentialProtector(new EphemeralDataProtectionProvider());
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
    public void CredentialProtector_ReturnsFailureForTamperedPayload()
    {
        var protector = new RegistryCredentialProtector(new EphemeralDataProtectionProvider());
        var protectedSecret = protector.Protect("secret");

        var result = protector.Unprotect(protectedSecret + "tampered");

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.IsType<InternalFailure>(failure);
    }

    private sealed class FakeContainerRegistryClient : IContainerRegistryClient
    {
        public const string BuildSecret = "raw-build-secret";
        public const string PullSecret = "raw-pull-secret";

        public int ProvisionCalls { get; private set; }
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());
    }
}
