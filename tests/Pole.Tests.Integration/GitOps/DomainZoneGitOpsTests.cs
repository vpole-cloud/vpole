using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application.Endpoints;
using Pole.Features.Domains.Domain;
using Pole.Features.Domains.Migrations;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Global)]
public sealed class DomainZoneGitOpsGlobalTests(MongoReplicaSetFixture fixture) : IsolatedDeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task DomainZoneCreatesThenNoOpsWithOwnershipAndNoBusinessEffects()
    {
        await EnablePublicWebTargetAsync();
        var created = await Apply(DomainZoneSnapshot());
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, created.Status);
        var zone = await DomainZones.Find(Builders<TenantDomainZone>.Filter.Empty).SingleAsync();
        Assert.Equal("company." + Data.Hostname("apps"), zone.Domain);
        var events = await BusinessEvents();
        var unchanged = await Apply(DomainZoneSnapshot());
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, unchanged.Status);
        var current = await DomainZones.Find(item => item.Id == zone.Id).SingleAsync();
        Assert.Equal(zone.Revision, current.Revision);
        Assert.Equal(zone.UpdatedAtUtc, current.UpdatedAtUtc);
        Assert.Equal(events, await BusinessEvents());
        var operations = (await Services.GetRequiredService<GitRepositoryQueries>().OperationsAsync(
            Data.TenantId, source, unchanged.Context!.RunId, 1, 30, default))!.Items;
        Assert.Equal(GitPlannedAction.NoOp, Assert.Single(operations).Plan!.Action);

        var detail = (await Mediator.Send(new GetDomainZoneRequest(zone.Id, Data.TenantId)))
            .AssertHandlerOutcome();
        Assert.NotNull(Assert.IsType<DomainZoneDto>(detail.Value).GitOps);
        var deletion = (await Mediator.Send(new DeleteDomainZoneRequest(zone.Id, Data.TenantId)))
            .AssertHandlerOutcome();
        Assert.IsType<DomainZoneGitOwned>(deletion.Value);
    }

    [Fact]
    public async Task DomainZoneImmutableAndMissingCreatedIdentitiesAreBlocked()
    {
        await EnablePublicWebTargetAsync();
        await Apply(DomainZoneSnapshot());
        var original = await DomainZones.Find(Builders<TenantDomainZone>.Filter.Empty).SingleAsync();
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(DomainZoneSnapshot("renamed"))).Status);
        await DomainZones.DeleteOneAsync(item => item.Id == original.Id);
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(DomainZoneSnapshot())).Status);
        Assert.Equal(0, await DomainZones.CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        await DomainZones.InsertOneAsync(original with { Id = Id() });
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(DomainZoneSnapshot())).Status);
        Assert.Equal(0, await DomainZones.CountDocumentsAsync(item => item.Id == original.Id));
    }

    [Fact]
    public async Task DomainZoneUnavailableOrReusedTargetDoesNotRebindAcceptedReference()
    {
        await EnablePublicWebTargetAsync();
        await Apply(DomainZoneSnapshot());
        var targets = Fixture.Database.GetCollection<DeploymentTarget>("DeploymentTargets");
        var target = await targets.Find(item => item.Id == TestData.DefaultDeploymentTargetId).SingleAsync();
        await targets.ReplaceOneAsync(item => item.Id == target.Id, target.SetEnabled(false, DateTime.UtcNow));
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(DomainZoneSnapshot())).Status);
        await targets.DeleteOneAsync(item => item.Id == target.Id);
        await targets.InsertOneAsync(target with { Id = Id() });
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(DomainZoneSnapshot())).Status);
        Assert.Equal(target.Id, (await DomainZones.Find(Builders<TenantDomainZone>.Filter.Empty).SingleAsync())
            .DeploymentTargetId);
    }

    [Fact]
    public async Task DomainZoneForeignDnsReservationDoesNotCreateResourceOrAppliedOwnership()
    {
        await EnablePublicWebTargetAsync();
        var manual = TenantDomainZoneFactory.CreateManagedReservation(Data.TenantId,
            TestData.DefaultDeploymentTargetId, "company", DnsName.Create(Data.Hostname("apps")), "manual-zone",
            new(Id(), DateTime.UtcNow));
        await DomainZones.InsertOneAsync(manual);
        var result = await Apply(DomainZoneSnapshot());
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(1, await DomainZones.CountDocumentsAsync(Builders<TenantDomainZone>.Filter.Empty));
        var inventory = await Inventory.Find(item => item.Kind == "DomainZone").SingleAsync();
        Assert.Equal(GitResourceMaterialization.Allocated, inventory.Materialization);
        Assert.Null(inventory.Projections.Applied);
        var receipt = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == result.Context!.RunId && item.Kind == "DomainZone").SingleAsync();
        Assert.Equal(nameof(DomainZoneDeclarationAlreadyReserved), receipt.Blocker);
        Assert.False(receipt.Changed);
        Assert.Equal(0, await DbContext.OutboxMessages.CountDocumentsAsync(item =>
            item.EventType == "pole.domains/v1/domain-metadata-changed"));
    }
}
