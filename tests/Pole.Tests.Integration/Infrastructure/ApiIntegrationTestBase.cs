using System.Collections.Immutable;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Domain;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

[Collection(IntegrationTestCollection.Name)]
public abstract class ApiIntegrationTestBase : IAsyncLifetime
{
    private TestApiApplication? _application;

    protected ApiIntegrationTestBase(MongoReplicaSetFixture fixture)
    {
        Fixture = fixture;
    }

    protected MongoReplicaSetFixture Fixture { get; }
    protected HttpClient Client => _application!.Client;
    protected virtual bool PrivateAccessEnabled => false;
    protected virtual void ConfigureTestServices(IServiceCollection services) { }
    protected Task StopApiAsync(CancellationToken cancellationToken) => _application!.StopAsync(cancellationToken);
    protected FakeGitHubAppClient GitHubClient => _application!.GitHubClient;
    protected void RejectNewDeploymentTargetWorkloads() =>
        _application!.RejectNewDeploymentTargetWorkloads();
    protected void AddEligibleDeploymentTarget(
        string deploymentTargetId,
        string publicBaseDomain) =>
        _application!.AddEligibleDeploymentTarget(
            deploymentTargetId,
            publicBaseDomain);
    protected void RejectDeploymentTargetReferenceRemoval() =>
        _application!.RejectDeploymentTargetReferenceRemoval();
    protected void SetPrivateAccessTargetState(
        bool enabled,
        bool deletionRequested,
        Pole.Features.DeploymentTargets.Contracts.NetworkPolicyReadiness readiness) =>
        _application!.SetPrivateAccessTargetState(enabled, deletionRequested, readiness);

    public virtual async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
        await SeedDefaultTenantAsync();
        await Fixture.DbContext.StorageProfiles.InsertOneAsync(TestData.SeedStorageProfile());
        _application = await TestApiApplication.StartAsync(Fixture, PrivateAccessEnabled, ConfigureTestServices);
    }

    public virtual async Task DisposeAsync()
    {
        if (_application is not null)
        {
            await _application.DisposeAsync();
        }
    }

    private async Task SeedDefaultTenantAsync()
    {
        var tenant = TenantFactory.Create("Test tenant", TestData.TenantId) with
        {
            Members = ImmutableList.Create(TenantFactory.CreateMember(
                TestData.TenantId,
                TestData.UserId,
                TestData.UserEmail,
                TenantRole.Owner))
        };

        await Fixture.DbContext.Tenants.InsertOneAsync(tenant);
        await Fixture.DbContext.Users.InsertOneAsync(PoleUserFactory.Create(
            TestData.UserId,
            TestData.UserEmail,
            null,
            null,
            TestData.TenantId,
            DateTime.UtcNow));
    }
}
