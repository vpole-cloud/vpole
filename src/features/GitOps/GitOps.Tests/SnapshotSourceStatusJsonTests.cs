using System.Text.Json;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class SnapshotSourceStatusJsonTests
{
    public static IEnumerable<object[]> States() => Enum.GetValues<RepositoryRunStatus>()
        .Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(States))]
    public void Current_run_status_has_a_typed_JSON_string_contract(RepositoryRunStatus state)
    {
        var view = new RepositoryRunView("run",
            "source",
            1,
            1,
            null,
            null,
            "Poll",
            null,
            state,
            DateTime.UtcNow,
            []);
        var json = JsonSerializer.Serialize(view, JsonSerializerOptions.Web);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(state.ToString(), document.RootElement.GetProperty("status").GetString());
        Assert.Equal(state, JsonSerializer.Deserialize<RepositoryRunView>(json, JsonSerializerOptions.Web)!.Status);
    }
}
