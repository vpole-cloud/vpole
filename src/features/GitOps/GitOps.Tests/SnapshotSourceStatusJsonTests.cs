using System.Text.Json;
using Pole.Features.GitOps.Application.Persistence;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class SnapshotSourceStatusJsonTests
{
    [Theory]
    [InlineData(SnapshotSourceState.AwaitingSnapshot, "AwaitingSnapshot")]
    [InlineData(SnapshotSourceState.Applying, "Applying")]
    [InlineData(SnapshotSourceState.Blocked, "Blocked")]
    [InlineData(SnapshotSourceState.ConfigurationApplied, "ConfigurationApplied")]
    [InlineData(SnapshotSourceState.UnsupportedStoredSchema, "UnsupportedStoredSchema")]
    [InlineData(SnapshotSourceState.StaleExecution, "StaleExecution")]
    public void Typed_source_status_preserves_the_JSON_string_contract(
        SnapshotSourceState state,
        string expectedJson)
    {
        var status = new SnapshotSourceStatus(
            "source", "https://example.test/source", 1, "run", state, "commit", null, null, [], []);
        var json = JsonSerializer.Serialize(status, JsonSerializerOptions.Web);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expectedJson, document.RootElement.GetProperty("status").GetString());
        var restored = JsonSerializer.Deserialize<SnapshotSourceStatus>(json, JsonSerializerOptions.Web)!;
        Assert.Equal(state, restored.Status);
    }
}
