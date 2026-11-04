using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.WebServices.Domain;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    private static ManifestFile[] WebConnectionSnapshot()
    {
        var documents = StageFiveWebSnapshot(_ => { })[0].Content.Split("\n---\n")
            .Select(item => JsonNode.Parse(item)!).ToList();
        var web = documents.Single(item => item["kind"]!.GetValue<string>() == "WebService");
        var peer = web.DeepClone();
        peer["metadata"]!["name"] = "backend";
        documents.Add(peer);
        web["spec"]!["connectionEnvironment"] = JsonNode.Parse("""
            {"PEER_HOST":{"connection":"api-backend","valueKind":"Host"},
             "PEER_PORT":{"connection":"api-backend","valueKind":"Port"}}
            """);
        documents.Add(JsonNode.Parse("""
            {"apiVersion":"pole.io/v1alpha1","kind":"ResourceConnection","metadata":{"name":"api-backend"},
             "spec":{"source":{"kind":"WebService","name":"demo-api"},
             "destination":{"kind":"WebService","name":"backend"},"endpoint":"http"}}
            """)!);
        return [new("web-connections.yaml", string.Join("\n---\n", documents.Select(item => item.ToJsonString())))];
    }

    [Fact]
    public async Task WebConnectionReferencesCreateInDependencyOrderRepairThenNoOp()
    {
        var files = WebConnectionSnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var web = await WebServices.Find(item => item.Name == "demo-api").SingleAsync();
        var connection = await Connections.Find(Builders<ResourceConnection>.Filter.Empty).SingleAsync();
        Assert.Equal(web.Id, connection.SourceResourceId);
        var expected = ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty
            .Add("PEER_HOST", new(connection.Id, "Host", null))
            .Add("PEER_PORT", new(connection.Id, "Port", null));
        Assert.Equal(expected.OrderBy(item => item.Key),
            web.Spec.Config.ConnectionEnvironmentReferences.OrderBy(item => item.Key));
        var releases = await Releases();
        await WebServices.ReplaceOneAsync(item => item.Id == web.Id, web with
        {
            Revision = web.Revision + 1,
            Spec = web.Spec with
            {
                Config = web.Spec.Config with
                { ConnectionEnvironmentReferences = expected.SetItem("PEER_HOST", new(connection.Id, "Port", null)) }
            }
        });
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var repaired = await WebServices.Find(item => item.Id == web.Id).SingleAsync();
        Assert.Equal(expected.OrderBy(item => item.Key),
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
        documents.Single(item => item["kind"]!.GetValue<string>() == "WebService")["spec"]!
            ["connectionEnvironment"] = JsonNode.Parse("""
                {"SELF":{"connection":"api-loopback","valueKind":"Host"}}
                """);
        files[0] = files[0] with { Content = string.Join("\n---\n", documents.Select(item => item.ToJsonString())) };
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, (await Apply(files)).Status);
        Assert.Equal(0, await WebServices.CountDocumentsAsync(Builders<WebService>.Filter.Empty));
        Assert.Equal(0, await Inventory.CountDocumentsAsync(Builders<Pole.Features.GitOps.Domain.ManagedDeclaration>
            .Filter.Empty));
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
        Assert.Equal(0, await WebServices.CountDocumentsAsync(Builders<WebService>.Filter.Empty));
    }
}
