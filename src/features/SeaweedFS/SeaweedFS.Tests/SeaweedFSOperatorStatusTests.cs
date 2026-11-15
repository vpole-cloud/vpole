using System.Text.Json.Nodes;
using Pole.Features.SeaweedFS.Application.Kubernetes;
using Xunit;

namespace Pole.Features.SeaweedFS.Tests;

public sealed class SeaweedFSOperatorStatusTests
{
    [Fact]
    public void Defaulted_fields_inside_a_CRD_array_do_not_trigger_endless_replacements()
    {
        var desired = JsonNode.Parse("""{"ports":[{"port":8333,"name":null}]}""");
        var current = JsonNode.Parse("""{"ports":[{"port":8333,"protocol":"TCP"}],"addedByOperator":true}""");

        Assert.True(SeaweedFSCustomObjects.Contains(current, desired));
        current!["ports"]![0]!["port"] = 8334;
        Assert.False(SeaweedFSCustomObjects.Contains(current, desired));
        current["ports"] = new JsonArray();
        Assert.False(SeaweedFSCustomObjects.Contains(current, desired));
    }

    [Fact]
    public void Ready_requires_the_operator_to_acknowledge_the_current_generation()
    {
        var current = JsonNode.Parse(
                """{"metadata":{"generation":2},"status":{"observedGeneration":1,"conditions":[{"type":"Ready","status":"True"}]}}""")
            !.AsObject();

        Assert.False(SeaweedFSCustomObjects.Ready(current));
        current["status"]!["observedGeneration"] = 2L;
        Assert.True(SeaweedFSCustomObjects.Ready(current));
    }
}
