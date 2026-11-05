using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Application.Endpoints;
using Pole.Features.ResourceConnections.Application.Common;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceConnections.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    private IMongoCollection<ResourceConnection> Connections =>
        Fixture.Database.GetCollection<ResourceConnection>("ResourceConnections");

    private static ManifestFile[] ConnectionSnapshot(string endpoint = "http") =>
    [
        .. StageFiveWebSnapshot(_ => { }),
        new("connection.yaml", $$"""
            apiVersion: pole.io/v1alpha1
            kind: ResourceConnection
            metadata:
              name: api-loopback
            spec:
              source: { kind: WebService, name: demo-api }
              destination: { kind: WebService, name: demo-api }
              endpoint: {{endpoint}}
            """)
    ];

    [Fact]
    public async Task ConnectionCreatesInPipelineThenNoOpsWithoutBusinessEventsAndBlocksManualDeletion()
    {
        await new ResourceConnectionIndexesMigration().Up(Fixture.Database);
        var files = ConnectionSnapshot();
        var created = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, created.Status);
        var connection = await Connections.Find(Builders<ResourceConnection>.Filter.Empty).SingleAsync();
        var service = await Service();
        Assert.Equal(service.Id, connection.SourceResourceId);
        Assert.Equal(service.Id, connection.DestinationResourceId);
        var owned = await Inventory.Find(item => item.ResourceId == connection.Id).SingleAsync();
        Assert.Equal(GitResourceMaterialization.Created, owned.Materialization);
        var events = await BusinessEvents();
        var releases = await Releases();

        var unchanged = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, unchanged.Status);
        var stable = await Connections.Find(item => item.Id == connection.Id).SingleAsync();
        Assert.Equal(connection.Revision, stable.Revision);
        Assert.Equal(connection.Generation, stable.Generation);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(releases, await Releases());
        var operations = (await Services.GetRequiredService<GitRepositoryQueries>().OperationsAsync(
            TestData.TenantId, source, unchanged.Context!.RunId, 1, 30, default))!.Items;
        Assert.Equal(GitPlannedAction.NoOp, operations.Single(item => item.Kind == "ResourceConnection").Plan!.Action);

        var deletion = await Services.GetRequiredService<BeginResourceConnectionDeletion>()(
            connection.TenantId, connection.Id, default);
        Assert.IsType<ResourceConnectionGitOwned>(deletion.Value);
        var detail = (await Mediator.Send(new GetResourceConnectionRequest(connection.Id, connection.TenantId)))
            .AssertHandlerOutcome();
        Assert.NotNull(Assert.IsType<ResourceConnectionDto>(detail.Value).GitOps);
    }

    [Fact]
    public async Task ConnectionMissingCreatedIdentityIsNotRecreatedOrRebound()
    {
        var files = ConnectionSnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var original = await Connections.Find(Builders<ResourceConnection>.Filter.Empty).SingleAsync();
        await Connections.DeleteOneAsync(item => item.Id == original.Id);
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(files)).Status);
        Assert.Equal(0, await Connections.CountDocumentsAsync(Builders<ResourceConnection>.Filter.Empty));
        await Connections.InsertOneAsync(original with { Id = Id() });
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(files)).Status);
        Assert.Equal(0, await Connections.CountDocumentsAsync(item => item.Id == original.Id));
    }

    [Fact]
    public async Task ConnectionRuntimeObservationDoesNotBecomeConfigurationDrift()
    {
        var files = ConnectionSnapshot();
        await Apply(files);
        var original = await Connections.Find(Builders<ResourceConnection>.Filter.Empty).SingleAsync();
        var target = Assert.IsType<ActiveResourceConnection>(original.Lifecycle).Targets.Single();
        var recorded = Assert.IsType<ConnectionApplyRecorded>(original.RecordApplied(target.Snapshot.TargetId,
            original.Generation, DateTime.UtcNow).Value).Connection;
        await Connections.ReplaceOneAsync(item => item.Id == original.Id, recorded);
        var events = await BusinessEvents();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var current = await Connections.Find(item => item.Id == original.Id).SingleAsync();
        Assert.Equal(recorded.Revision, current.Revision);
        Assert.Equal(recorded.Generation, current.Generation);
        Assert.IsType<AppliedConnectionApply>(Assert.IsType<ActiveResourceConnection>(current.Lifecycle)
            .Targets.Single().Status);
        Assert.Equal(events, await BusinessEvents());
    }

    [Fact]
    public async Task ConnectionUnavailableEndpointDoesNotWriteOwnershipCreationReceiptOrEvent()
    {
        var result = await Apply(ConnectionSnapshot("missing-endpoint"));
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(0, await Connections.CountDocumentsAsync(Builders<ResourceConnection>.Filter.Empty));
        var owned = await Inventory.Find(item => item.Kind == "ResourceConnection").SingleAsync();
        Assert.Equal(GitResourceMaterialization.Allocated, owned.Materialization);
        Assert.Null(owned.Projections.Applied);
        var receipt = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == result.Context!.RunId && item.Kind == "ResourceConnection").SingleAsync();
        Assert.False(receipt.Changed);
        Assert.Equal(nameof(ConnectionDeclarationEndpointUnavailable), receipt.Blocker);
        Assert.Equal(0, await DbContext.OutboxMessages.CountDocumentsAsync(item =>
            item.EventType == "pole.resource-connections/v1/resource-connection-created"));
    }
}
