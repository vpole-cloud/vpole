using System.Text.Json;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class RealtimeJournalTests
{
    [Theory]
    [InlineData("false")]
    [InlineData("empty")]
    [InlineData("error")]
    public void Current_acknowledgements_replace_true_coverage(string next)
    {
        var journal = Covered();
        Sent(journal, "2", "Heartbeat");
        Received(journal, next switch
        {
            "empty" => """{"type":3,"invocationId":"2","result":[]}""",
            "error" => """{"type":3,"invocationId":"2","error":"rejected"}""",
            _ => Ack("2", false)
        });
        Assert.False(HasCoverage(journal));
    }

    [Theory]
    [InlineData("Subscribe")]
    [InlineData("Unsubscribe")]
    public void Changed_interests_fence_pending_heartbeat(string method)
    {
        var journal = Covered();
        Sent(journal, "2", "Heartbeat");
        Sent(journal, "3", method);
        Received(journal, Ack("2", true));
        Assert.False(HasCoverage(journal));
        Received(journal, Ack("3", true));
        Assert.Equal(method == "Subscribe", HasCoverage(journal));
    }

    [Fact]
    public void Older_completion_cannot_overwrite_newer_false_acknowledgement()
    {
        var journal = Covered();
        Sent(journal, "2", "Heartbeat");
        Sent(journal, "3", "Heartbeat");
        Received(journal, Ack("3", false));
        Received(journal, Ack("2", true));
        Assert.False(HasCoverage(journal));
    }

    [Fact]
    public void Replaced_resource_interests_do_not_keep_previous_resource_coverage()
    {
        var journal = Covered();
        Sent(journal, "2", "Heartbeat");
        Sent(journal, "3", "Subscribe");
        Received(journal, Ack("3", true).Replace("\"resource\"", "\"replacement\"", StringComparison.Ordinal));
        Received(journal, Ack("2", true));
        Assert.False(HasCoverage(journal));
        Assert.Equal(1, journal.LatestCoveredConnection(values => values.Any(value => value.Covered &&
            value.ResourceId == "replacement")));
    }

    [Theory]
    [InlineData("close")]
    [InlineData("navigate")]
    [InlineData("new-connection")]
    [InlineData("SessionRevoked")]
    [InlineData("AccessRevoked")]
    public void Retired_scope_cannot_be_resurrected_by_late_completion(string transition)
    {
        var journal = Covered();
        Sent(journal, "2", "Heartbeat");
        switch (transition)
        {
            case "close": journal.Close(1); break;
            case "navigate": journal.Navigate(); break;
            case "new-connection": journal.Open(2, 20); break;
            default: Received(journal, JsonSerializer.Serialize(new { target = transition })); break;
        }

        Received(journal, Ack("2", true));
        Assert.False(HasCoverage(journal));
    }

    [Fact]
    public void Acknowledgements_require_matching_invocation_and_resource()
    {
        var journal = new RealtimeCoverageJournal();
        journal.Open(1, 10);
        Received(journal, Ack("unknown", true));
        Assert.False(HasCoverage(journal));
        Sent(journal, "1", "Subscribe");
        Received(journal, Ack("1", true));
        Assert.True(HasCoverage(journal));
        Assert.Equal(0, journal.LatestCoveredConnection(values => values.Any(value => value.ResourceId == "other")));
        Assert.Equal(0, journal.LatestCoveredConnection(_ => true, after: 10, requireOpenedAfter: true));
    }

    [Theory]
    [InlineData(200, false, false, (int)HttpReadOutcome.Succeeded)]
    [InlineData(403, false, false, (int)HttpReadOutcome.Rejected)]
    [InlineData(503, false, false, (int)HttpReadOutcome.Rejected)]
    [InlineData(200, true, false, (int)HttpReadOutcome.TransportFailed)]
    [InlineData(200, true, true, (int)HttpReadOutcome.Cancelled)]
    [InlineData(0, true, false, (int)HttpReadOutcome.TransportFailed)]
    public void Only_completed_successful_reads_count_as_quiet_evidence(int status,
        bool failed,
        bool cancelled,
        int outcome)
    {
        var journal = new HttpReadJournal<string>();
        journal.Start("request", "/metadata", 1);
        Assert.False(journal.Quiet(_ => true, TimeSpan.Zero));
        if (status != 0)
            journal.Response("request", status);
        Assert.False(journal.Quiet(_ => true, TimeSpan.Zero));
        journal.Finish("request", failed, cancelled);
        Assert.Equal((HttpReadOutcome)outcome, Assert.Single(journal.Snapshot()).Outcome);
        Assert.Equal((HttpReadOutcome)outcome == HttpReadOutcome.Succeeded, journal.Quiet(_ => true, TimeSpan.Zero));
    }

    [Fact]
    public void Recovery_must_finish_and_does_not_hide_an_older_pending_read()
    {
        var journal = new HttpReadJournal<string>();
        journal.Start("old", "/metadata", 1);
        journal.Start("recovery", "/metadata", 2);
        journal.Response("recovery", 200);
        journal.Finish("recovery");
        Assert.False(journal.Quiet(_ => true, TimeSpan.Zero));
        journal.Finish("old", cancelled: true);
        Assert.True(journal.Quiet(_ => true, TimeSpan.Zero));
        Assert.Equal(HttpReadOutcome.Cancelled, journal.Snapshot()[0].Outcome);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("cancelled")]
    [InlineData("succeeded")]
    public void Retired_document_reads_cannot_block_current_success_or_restore_evidence(string lateOutcome)
    {
        var journal = new HttpReadJournal<string>();
        journal.Start("old-inventory", "/inventory", 1);
        journal.Navigate();
        Assert.False(journal.Quiet(_ => true, TimeSpan.Zero));
        if (lateOutcome != "pending")
        {
            journal.Response("old-inventory", 200);
            journal.Finish("old-inventory", cancelled: lateOutcome == "cancelled");
        }

        // Even a late successful response cannot establish evidence for the new document.
        Assert.False(journal.Quiet(_ => true, TimeSpan.Zero));
        journal.Start("detail", "/detail", 2);
        journal.Response("detail", 200);
        journal.Finish("detail");
        Assert.True(journal.Quiet(_ => true, TimeSpan.Zero));
        Assert.Equal(2, journal.Snapshot().Count);
        Assert.NotEqual(journal.Snapshot()[0].Document, journal.Snapshot()[1].Document);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("succeeded")]
    public void Late_retired_completion_keeps_current_quiet_and_retains_its_outcome(string outcome)
    {
        var journal = new HttpReadJournal<string>();
        journal.Start("old-inventory", "/inventory", 1);
        journal.Navigate();
        journal.Start("detail", "/detail", 2);
        journal.Response("detail", 200);
        journal.Finish("detail");
        Assert.True(journal.Quiet(_ => true, TimeSpan.Zero));
        journal.Response("old-inventory", outcome == "rejected" ? 503 : 200);
        journal.Finish("old-inventory", cancelled: outcome == "cancelled");
        Assert.True(journal.Quiet(_ => true, TimeSpan.Zero));
        Assert.Equal(outcome switch
        {
            "cancelled" => HttpReadOutcome.Cancelled,
            "rejected" => HttpReadOutcome.Rejected,
            _ => HttpReadOutcome.Succeeded
        }, journal.Snapshot()[0].Outcome);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("transport")]
    public void Current_document_pending_and_failed_reads_still_block_quiet(string outcome)
    {
        var journal = new HttpReadJournal<string>();
        journal.Start("old", "/inventory", 1);
        journal.Response("old", 200);
        journal.Finish("old");
        journal.Navigate();
        journal.Start("detail", "/detail", 2);
        journal.Response("detail", 200);
        journal.Finish("detail");
        journal.Start("current", "/inventory", 3);
        if (outcome != "pending")
        {
            journal.Response("current", outcome == "rejected" ? 503 : 200);
            journal.Finish("current", failed: outcome == "transport", cancelled: outcome == "cancelled");
        }

        Assert.False(journal.Quiet(_ => true, TimeSpan.Zero));
    }

    private static RealtimeCoverageJournal Covered()
    {
        var journal = new RealtimeCoverageJournal();
        journal.Open(1, 10);
        Sent(journal, "1", "Subscribe");
        Received(journal, Ack("1", true));
        Assert.True(HasCoverage(journal));
        return journal;
    }

    private static bool HasCoverage(RealtimeCoverageJournal journal) => journal.LatestCoveredConnection(values =>
        values.Any(value => value.Covered && value.Topic == "operations" && value.TenantId == "tenant" &&
            value.ResourceId == "resource")) > 0;

    private static void Sent(RealtimeCoverageJournal journal, string id, string method)
    {
        using var frame = JsonDocument.Parse(JsonSerializer.Serialize(new { type = 1, invocationId = id, target = method }));
        journal.Sent(1, frame.RootElement);
    }

    private static void Received(RealtimeCoverageJournal journal, string json)
    {
        using var frame = JsonDocument.Parse(json);
        journal.Received(1, frame.RootElement, 30);
    }

    private static string Ack(string id, bool covered) => JsonSerializer.Serialize(new
    {
        type = 3,
        invocationId = id,
        result = new[] { new { topic = "operations", tenantId = "tenant", resourceId = "resource", covered } }
    });
}
