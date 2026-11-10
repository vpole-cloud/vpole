using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using Pole.Features.GitOps.Domain;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant1)]
public sealed class WebServiceConnectionDeclarationTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task WebConnectionReferencesCreateInDependencyOrderRepairThenNoOp()
    {
        var files = WebConnectionSnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var web = await WebServices.Find(item => item.TenantId == Data.TenantId && item.Name == "demo-api")
            .SingleAsync();
        var connection = await Connections
            .Find(Builders<ResourceConnection>.Filter.Eq(item => item.TenantId, Data.TenantId))
            .SingleAsync();
        Assert.Equal(web.Id, connection.SourceResourceId);
        var expected = ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty
            .Add("PEER_HOST", new ConnectionEnvironmentReference(connection.Id, "Host", null))
            .Add("PEER_PORT", new ConnectionEnvironmentReference(connection.Id, "Port", null));
        Assert.Equal(
            expected.OrderBy(item => item.Key),
            web.Spec.Config.ConnectionEnvironmentReferences.OrderBy(item => item.Key));
        var releases = await Releases();
        await WebServices.ReplaceOneAsync(
            item => item.Id == web.Id,
            web with
            {
                Revision = web.Revision + 1,
                Spec = web.Spec with
                {
                    Config = web.Spec.Config with
                    {
                        ConnectionEnvironmentReferences = expected.SetItem(
                            "PEER_HOST",
                            new ConnectionEnvironmentReference(connection.Id, "Port", null)),
                    },
                },
            });
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var repaired = await WebServices.Find(item => item.Id == web.Id).SingleAsync();
        Assert.Equal(
            expected.OrderBy(item => item.Key),
            repaired.Spec.Config.ConnectionEnvironmentReferences.OrderBy(item => item.Key));
        Assert.Equal(releases + 1, await Releases());
        var events = await BusinessEvents();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        Assert.Equal(repaired.Revision, (await WebServices.Find(item => item.Id == web.Id).SingleAsync()).Revision);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(releases + 1, await Releases());
    }

    [Fact]
    public async Task WebConnectionDependencyCycleFailsBeforeAnyMutation()
    {
        var files = ConnectionSnapshot();
        var documents = files[0].Content.Split("\n---\n").Select(item => JsonNode.Parse(item)!).ToArray();
        documents.Single(item => item["kind"]!.GetValue<string>() == "WebService")["spec"]!["connectionEnvironment"]
            = JsonNode.Parse(
                """
                {"SELF":{"connection":"api-loopback","valueKind":"Host"}}
                """);
        files[0] = files[0] with { Content = string.Join("\n---\n", documents.Select(item => item.ToJsonString())) };
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, (await Apply(files)).Status);
        Assert.Equal(
            0,
            await WebServices.CountDocumentsAsync(
                Builders<WebService>.Filter.Eq(item => item.TenantId, Data.TenantId)));
        Assert.Equal(
            0,
            await Inventory.CountDocumentsAsync(
                Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, Data.TenantId)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("Configuration")]
    public async Task WebConnectionUnsupportedFieldIsRejectedWithoutAcceptingConfiguration(string valueKind)
    {
        var files = WebConnectionSnapshot();
        var documents = files[0].Content.Split("\n---\n").Select(item => JsonNode.Parse(item)!).ToArray();
        var spec = documents.Single(item => item["kind"]!.GetValue<string>() == "WebService" &&
            item["metadata"]!["name"]!.GetValue<string>() == "demo-api")["spec"]!;
        spec["connectionEnvironment"]!["PEER_HOST"]!["valueKind"] = valueKind;
        files[0] = files[0] with { Content = string.Join("\n---\n", documents.Select(item => item.ToJsonString())) };
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, (await Apply(files)).Status);
        Assert.Equal(
            0,
            await WebServices.CountDocumentsAsync(
                Builders<WebService>.Filter.Eq(item => item.TenantId, Data.TenantId)));
    }
}
