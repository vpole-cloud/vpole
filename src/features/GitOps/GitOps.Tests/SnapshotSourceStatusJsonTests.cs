// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using Pole.ASP.Helpers;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class SnapshotSourceStatusJsonTests
{
    private static readonly JsonSerializerOptions HttpOptions = PoleHttpJson.CreateSerializerOptions();

    public static IEnumerable<object[]> Actions() => Enum.GetValues<GitPlannedAction>()
        .Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(Actions))]
    public void Operation_plan_has_a_typed_JSON_action_contract(GitPlannedAction action)
    {
        var plan = new RepositoryOperationPlanView(action, 1, DateTime.UnixEpoch, ["configuration.description"]);
        var json = JsonSerializer.Serialize(plan, HttpOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(action.ToString(), document.RootElement.GetProperty("action").GetString());
        Assert.Equal(action, JsonSerializer.Deserialize<RepositoryOperationPlanView>(json, HttpOptions)!.Action);
        var numeric = JsonNode.Parse(json)!;
        numeric["action"] = (int)action;
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<RepositoryOperationPlanView>(numeric.ToJsonString(), HttpOptions));
    }

    public static IEnumerable<object[]> States() => Enum.GetValues<RepositoryRunStatus>()
        .Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(States))]
    public void Current_run_status_has_a_typed_JSON_string_contract(RepositoryRunStatus state)
    {
        var view = new RepositoryRunView(
            "run",
            "source",
            1,
            1,
            null,
            null,
            "Poll",
            null,
            state,
            DateTime.UtcNow,
            [],
            null,
            null);
        var json = JsonSerializer.Serialize(view, HttpOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(state.ToString(), document.RootElement.GetProperty("status").GetString());
        Assert.Equal(state, JsonSerializer.Deserialize<RepositoryRunView>(json, HttpOptions)!.Status);
    }
}
