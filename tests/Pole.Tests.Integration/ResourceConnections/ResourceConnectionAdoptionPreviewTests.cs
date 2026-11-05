using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceConnections.Application.Common;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceConnections;

public sealed class ResourceConnectionAdoptionPreviewTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private IDeclarativeAdoptionPreviewProvider Provider =>
        Assert.IsAssignableFrom<IDeclarativeAdoptionPreviewProvider>(
        Services.GetServices<IDeclarativeResourceProvider>().Single(item => item.Kind == "ResourceConnection"));

    [Fact]
    public async Task Preview_preserves_identity_and_uses_only_the_supplied_reference_ids_without_writes()
    {
        var connection = await CreateConnection();
        var request = Request(connection);
        var before = await Fixture.Database.GetCollection<BsonDocument>("ResourceConnections")
            .Find(item => item["_id"] == ObjectId.Parse(connection.Id)).SingleAsync();
        var events = await Fixture.Database.GetCollection<BsonDocument>("OutboxMessages")
            .CountDocumentsAsync(_ => true);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var result = Assert.IsType<GitLifecycleAdoptionPreview>(
            (await Provider.PreviewAdoptionAsync(request, session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.Equal(connection.Id, result.Current.ResourceId);
        Assert.True(System.Text.Json.JsonElement.DeepEquals(result.Current.Projection.Identity,
            result.Desired.Identity));
        Assert.Equal(before, await Fixture.Database.GetCollection<BsonDocument>("ResourceConnections")
            .Find(item => item["_id"] == ObjectId.Parse(connection.Id)).SingleAsync());
        Assert.Equal(events, await Fixture.Database.GetCollection<BsonDocument>("OutboxMessages")
            .CountDocumentsAsync(_ => true));
    }

    [Theory]
    [InlineData("missing-reference")]
    [InlineData("wrong-tenant")]
    [InlineData("missing-id")]
    [InlineData("deleting")]
    public async Task Preview_blocks_unaccepted_references_or_unavailable_identity(string change)
    {
        var connection = await CreateConnection();
        var request = Request(connection);
        request = change switch
        {
            "missing-reference" => request with { References = [] },
            "changed-reference" => request with
            {
                References = [request.References[0] with { ResourceId = connection.DestinationResourceId },
                    request.References[1]]
            },
            "wrong-tenant" => request with { TenantId = ObjectId.GenerateNewId().ToString() },
            "missing-id" => request with { ResourceId = ObjectId.GenerateNewId().ToString() },
            "deleting" => request,
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        if (change == "deleting")
            await Fixture.Database.GetCollection<ResourceConnection>("ResourceConnections")
                .ReplaceOneAsync(item => item.Id == connection.Id, connection.BeginDeletion(DateTime.UtcNow));
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var result = await Provider.PreviewAdoptionAsync(request, session, default);
        Assert.IsType<DeclarationReadBlocked>(result.Value);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task Changed_accepted_identity_is_returned_as_a_projection_for_explicit_replacement_planning()
    {
        var connection = await CreateConnection();
        var request = Request(connection);
        request = request with
        {
            References = [request.References[0] with { ResourceId = connection.DestinationResourceId },
                request.References[1]]
        };
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var result = Assert.IsType<GitLifecycleAdoptionPreview>(
            (await Provider.PreviewAdoptionAsync(request, session, default)).Value);
        Assert.Equal(connection.SourceResourceId, result.Current.Projection.Identity.GetProperty("sourceId")
            .GetString());
        Assert.Equal(connection.DestinationResourceId, result.Desired.Identity.GetProperty("sourceId").GetString());
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task Replacement_projects_engine_reserved_ids_before_their_endpoints_exist()
    {
        var connection = await CreateConnection();
        var request = Request(connection);
        var reserved = ObjectId.GenerateNewId().ToString();
        request = request with
        {
            References = [request.References[0] with { ResourceId = reserved }, request.References[1]]
        };
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var result = Assert.IsType<GitLifecycleAdoptionPreview>(
            (await Provider.PreviewAdoptionAsync(request, session, default)).Value);
        Assert.Equal(reserved, result.Desired.Identity.GetProperty("sourceId").GetString());
        Assert.Equal(connection.SourceResourceId, result.Current.Projection.Identity.GetProperty("sourceId")
            .GetString());
        await session.AbortTransactionAsync();
    }

    private async Task<ResourceConnection> CreateConnection()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var source = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id, "caller"))).AssertSuccess();
        var destination = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id, "receiver"))).AssertSuccess();
        var creation = Services.GetRequiredService<PersistResourceConnectionCreation>();
        return Assert.IsType<ResourceConnection>((await creation(
            new(TestData.TenantId, "caller-to-receiver", WebServiceResourceDeployment.ResourceType,
                source.WebServiceId, WebServiceResourceDeployment.ResourceType,
                destination.WebServiceId, "http"))).Value);
    }

    private static PreviewLifecycleAdoptionRequest Request(ResourceConnection connection)
    {
        var spec = new ResourceConnectionDeclaration(new("WebService", "caller"),
            new("WebService", "receiver"), "http");
        return new(connection.TenantId, new(new("ResourceConnection", connection.Name), DeclarationJson.Write(spec),
            "desired", [], "connection.yaml", 1), connection.Id,
            [new(spec.Source, connection.SourceResourceId), new(spec.Destination, connection.DestinationResourceId)]);
    }
}
