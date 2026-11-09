using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.Domains.Migrations;
using Pole.Features.Outbox.Application;
using Pole.Features.Outbox.Application.Mongo;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Domains;

[Collection(IntegrationTestCollection.Name)]
public sealed class GeneratedDomainZoneIdentityTests(MongoReplicaSetFixture fixture) : MongoIntegrationTestBase(fixture)
{
    private const string FirstTenant = "6a0000000000000000000001";
    private const string SecondTenant = "6a0000000000000000000002";
    private const string Target = "6a0000000000000000000003";
    private ServiceProvider _services = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new DomainIndexesMigration().Up(Database);
        await new TenantDomainZoneSchemaValidationMigration().Up(Database);
        _services = new ServiceCollection().AddSingleton(Database).AddOutboxPersistence().BuildServiceProvider();
    }

    public override async Task DisposeAsync()
    {
        try
        {
            if (_services is not null) await _services.DisposeAsync();
        }
        finally
        {
            await base.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameNameAndObjectIdTimestampDoNotCollideAcrossTenants(bool longName)
    {
        var name = longName ? new string('a', 100) : "Acme Corporation";
        var first = Success(await GetOrCreateAsync(FirstTenant, name));
        var second = Success(await GetOrCreateAsync(SecondTenant, name));

        Assert.NotEqual(first.Domain, second.Domain);
        Assert.EndsWith("-" + FirstTenant, first.Label);
        Assert.EndsWith("-" + SecondTenant, second.Label);
        Assert.True(DnsLabel.IsValid(first.Label));
        Assert.True(DnsLabel.IsValid(second.Label));
        if (longName) Assert.Equal(DnsLabel.MaximumLength, second.Label.Length);
        Assert.Equal(2, await Database.GetCollection<TenantDomainZone>("TenantDomainZones")
            .CountDocumentsAsync(FilterDefinition<TenantDomainZone>.Empty));
        var reused = Success(await GetOrCreateAsync(FirstTenant, name));
        Assert.Equal(first.Id, reused.Id);
        Assert.Equal(first.Domain, reused.Domain);
        Assert.Equal(first.Revision, reused.Revision);
        Assert.Equal(2, await DbContext.OutboxMessages.CountDocumentsAsync(message => message.EventType == "pole.domains/v1/domain-metadata-changed"));
    }

    [Fact]
    public async Task ExistingLegacyZoneKeepsItsIdentityAndDoesNotEmitAnotherChange()
    {
        var legacy = await SeedLegacyAsync();
        var reused = Success(await GetOrCreateAsync(FirstTenant, "Acme Corporation"));

        Assert.Equal(legacy.Id, reused.Id);
        Assert.Equal(legacy.Label, reused.Label);
        Assert.Equal(legacy.Domain, reused.Domain);
        Assert.Equal(0, await DbContext.OutboxMessages.CountDocumentsAsync(_ => true));
    }

    [Theory]
    [InlineData("Acme Corporation", "different.example.test")]
    [InlineData("Renamed tenant", "example.test")]
    public async Task LegacyCompatibilityDoesNotAcceptAnotherBaseDomainOrTenantName(string name, string baseDomain)
    {
        await SeedLegacyAsync();
        var error = Assert.IsType<GeneratedDomainZoneError>((await GetOrCreateAsync(FirstTenant, name, baseDomain)).Value);
        Assert.IsType<GeneratedDomainZoneMismatch>(error.Value);
        Assert.Equal(0, await DbContext.OutboxMessages.CountDocumentsAsync(_ => true));
    }

    private async Task<TenantDomainZone> SeedLegacyAsync()
    {
        var zone = TenantDomainZoneFactory.CreateGenerated(FirstTenant, Target,
            "pole-generated-acme-corporation-" + FirstTenant[..8], DnsName.Create("example.test"),
            new("6a0000000000000000000004", DateTime.UtcNow));
        await Database.GetCollection<TenantDomainZone>("TenantDomainZones").InsertOneAsync(zone);
        return zone;
    }

    private Task<GetOrCreateGeneratedDomainZonesOutcome> GetOrCreateAsync(string tenantId, string name, string baseDomain = "example.test")
    {
        var capability = new MongoGetOrCreateGeneratedDomainZones(
            Database.GetCollection<TenantDomainZone>("TenantDomainZones"),
            (_, session, _) =>
            {
                Assert.True(session.IsInTransaction);
                return Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(new DeploymentTargetReferencesChanged());
            },
            (_, session, _) =>
            {
                Assert.True(session.IsInTransaction);
                return Task.FromResult<FenceDeploymentTargetWorkloadConfigurationsOutcome>(new DeploymentTargetWorkloadConfigurationsFenced());
            },
            (requested, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(requested, name)),
            TimeProvider.System,
            _services.GetRequiredService<StoreOutboxWrites>());
        return new MongoRunTransaction(Database.Client).InvokeOutcomeAsync<GetOrCreateGeneratedDomainZonesOutcome>(async (transaction, token) =>
        {
            var outcome = await capability.InvokeAsync([new(tenantId, Target, baseDomain, 1)], transaction.Session, token);
            return outcome is IReadOnlyList<TenantDomainZoneSnapshot> ? transaction.Commit(outcome) : transaction.Abort(outcome);
        });
    }

    private static TenantDomainZoneSnapshot Success(GetOrCreateGeneratedDomainZonesOutcome outcome) =>
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TenantDomainZoneSnapshot>>(outcome.Value));
}
