// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class BrowserPhaseJournalTests
{
    [Fact]
    public void Correlates_the_exact_deployment_uid_container_and_read_mode()
    {
        const string url
            = "https://localhost/api/v1/resource-deployments/deployment/runtime-logs?instanceUid=replacement&container=log-a&follow=true&expectedRevision=7";
        Assert.True(BrowserPhaseJournal.IsRuntimeUrl(url, "deployment", "replacement", "log-a", true));
        Assert.False(BrowserPhaseJournal.IsRuntimeUrl(url, "deployment", "old-uid", "log-a", true));
        Assert.False(BrowserPhaseJournal.IsRuntimeUrl(url, "deployment", "replacement", "log-b", true));
        Assert.False(BrowserPhaseJournal.IsRuntimeUrl(url, "another", "replacement", "log-a", true));
        Assert.False(BrowserPhaseJournal.IsRuntimeUrl(url, "deployment", "replacement", "log-a", false));
        Assert.False(BrowserPhaseJournal.IsRuntimeUrl("not-a-url", "deployment", "replacement", "log-a", true));
    }

    [Fact]
    public async Task Request_deadline_does_not_expire_while_the_single_action_waits_for_eligibility()
    {
        var observation = new TestActionObservation<string>();
        var actionFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var result = observation.RunAsync(
            () =>
            {
                calls++;
                return actionFinished.Task;
            },
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.False(result.IsCompleted);
        Assert.True(observation.TryObserve("request"));
        actionFinished.SetResult();
        Assert.Equal("request", await result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Missing_request_fails_after_the_action_without_replaying_it()
    {
        var observation = new TestActionObservation<string>();
        var calls = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => observation.RunAsync(
            () =>
            {
                calls++;
                return Task.CompletedTask;
            },
            TimeSpan.Zero,
            CancellationToken.None));
        Assert.Equal(1, calls);
    }
}
