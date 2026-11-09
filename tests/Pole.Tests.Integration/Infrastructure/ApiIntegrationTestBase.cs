using System.Collections.Immutable;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Domain;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

public abstract class ApiIntegrationTestBase : IAsyncLifetime
{
    private TestApiApplication? _application;
    private readonly bool _isolatedDatabase;

    protected ApiIntegrationTestBase(MongoReplicaSetFixture fixture) : this(fixture, false) { }

    protected ApiIntegrationTestBase(
        MongoReplicaSetFixture fixture,
        bool isolatedDatabase,
        IntegrationSchemaProfile schemaProfile = IntegrationSchemaProfile.Full)
    {
        if (!isolatedDatabase && schemaProfile != IntegrationSchemaProfile.Full)
            throw new ArgumentException("Shared tenant databases require the Full schema profile.", nameof(schemaProfile));
        _isolatedDatabase = isolatedDatabase;
        Fixture = isolatedDatabase ? fixture.CreateDatabase(schemaProfile) : fixture.SharedDatabase;
        TestScope = IntegrationTestScope.Create(Fixture);
        Data = new IntegrationTestData(TestScope);
        Timings = new TestTimings(GetType());
    }

    protected TestDatabaseScope Fixture { get; }
    protected IntegrationTestScope TestScope { get; }
    protected IntegrationTestData Data { get; }
    protected TestTimings Timings { get; }
    protected HttpClient Client => _application!.Client;
    protected IServiceProvider ApiServices => _application!.Services;
    protected virtual bool PrivateAccessEnabled => false;

    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

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
        using (Timings.Measure("database.prepare"))
            await Fixture.PrepareAsync();
        using (Timings.Measure("seed.base"))
            await TestScope.SeedAsync();
        _application = await TestApiApplication.StartAsync(Fixture, TestScope, PrivateAccessEnabled, ConfigureTestServices, Timings);
    }

    public virtual async Task DisposeAsync()
    {
        try
        {
            if (_application is not null)
            {
                using (Timings.Measure("api.host.dispose"))
                    await _application.DisposeAsync();
            }
        }
        finally
        {
            if (_isolatedDatabase)
                using (Timings.Measure("database.dispose")) await Fixture.DisposeAsync();
        }
    }
}

public abstract class IsolatedApiIntegrationTestBase(
    MongoReplicaSetFixture fixture,
    IntegrationSchemaProfile schemaProfile = IntegrationSchemaProfile.Full)
    : ApiIntegrationTestBase(fixture, true, schemaProfile), IIsolatedIntegrationTest;
