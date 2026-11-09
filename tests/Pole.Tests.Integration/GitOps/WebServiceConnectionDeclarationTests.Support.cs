using Pole.Tests.Integration.Infrastructure;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.WebServices.Domain;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected static ManifestFile[] WebConnectionSnapshot()
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
}
