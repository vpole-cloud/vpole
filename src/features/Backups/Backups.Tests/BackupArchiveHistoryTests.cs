using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Backups.Domain;
using Xunit;

namespace Pole.Features.Backups.Tests;

public sealed class BackupArchiveHistoryTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly BackupExecutionOrigin Origin = new("trace", "correlation");
    private static readonly BackupScheduleSuspension Suspended = new("policy", 2, "publisher");

    [Fact]
    public void Native_drain_requires_a_complete_post_pause_snapshot_and_no_active_backups()
    {
        var live = BackupArchiveHistory.Begin(new("initial", Origin), Now);
        var drain = live.RequestDrain(Suspended, new("drain", Origin), Now);
        var first = drain.Accept(
            new PagedBackupHistoryPosition(new("publisher", "opaque:snapshot"), "page-2"),
            [new("scheduled", true), new("manual", true)],
            Now,
            TimeSpan.Zero);
        Assert.IsType<MonitoringBackupHistory>(first);
        var listed = first.Accept(
            new FollowingBackupHistoryPosition(new("publisher", "opaque:snapshot")),
            [new("scheduled", false)],
            Now,
            TimeSpan.Zero);
        Assert.IsType<MonitoringBackupHistory>(listed);
        var complete = listed.Accept(
            new FollowingBackupHistoryPosition(new("publisher", "opaque:bookmark")),
            [new("manual", false)],
            Now,
            TimeSpan.Zero);
        var quiescent = Assert.IsType<QuiescentBackupHistory>(complete);
        Assert.Equal(Suspended, quiescent.Suspension);
        Assert.Equal("opaque:bookmark", quiescent.Stream.Version);
        Assert.Equal(Origin, quiescent.Origin);
        Assert.Throws<InvalidOperationException>(() => complete.Claim(new("claim", Now.AddMinutes(1))));
        Assert.Throws<InvalidOperationException>(() => complete.Retry(Now, TimeSpan.Zero, "failed"));
    }

    [Fact]
    public void Empty_partial_page_and_live_history_do_not_authorize_a_drain()
    {
        var live = BackupArchiveHistory.Begin(new("initial", Origin), Now);
        var observed = live.Accept(new FollowingBackupHistoryPosition(new("publisher", "1")), [], Now, TimeSpan.Zero);
        Assert.IsType<MonitoringBackupHistory>(observed);
        var drain = observed.RequestDrain(Suspended, new("post-pause", Origin), Now);
        Assert.IsType<InitialBackupHistoryPosition>(Assert.IsType<MonitoringBackupHistory>(drain).Checkpoint.Position);
        var partial = drain.Accept(
            new PagedBackupHistoryPosition(new("publisher", "2"), "next"),
            [],
            Now,
            TimeSpan.Zero);
        Assert.IsType<MonitoringBackupHistory>(partial);
    }

    [Fact]
    public void Replayed_drain_request_preserves_its_claim_and_checkpoint_but_a_new_fence_invalidates_them()
    {
        var drain = BackupArchiveHistory.Begin(new("initial", Origin), Now)
            .RequestDrain(Suspended, new("drain", Origin), Now);
        var claimed = drain.Claim(new("lease", Now.AddMinutes(1)));
        Assert.Same(claimed, claimed.RequestDrain(Suspended, new("unused", Origin), Now));
        var changed = Assert.IsType<MonitoringBackupHistory>(
            claimed.RequestDrain(Suspended with { PolicyRevision = 3 }, new("new-epoch", Origin), Now));
        Assert.True(changed.Revision > claimed.Revision);
        Assert.Equal("new-epoch", changed.Checkpoint.ScanId);
        Assert.IsType<AvailableBackupDispatch>(changed.Dispatch);
    }

    [Fact]
    public void Expired_cursor_restarts_a_new_epoch_and_requires_a_new_consistent_snapshot()
    {
        var drain = BackupArchiveHistory.Begin(new("initial", Origin), Now)
            .RequestDrain(Suspended, new("drain", Origin), Now);
        var observed = drain.Accept(
            new FollowingBackupHistoryPosition(new("publisher", "rv-old")),
            [new("old", true)],
            Now,
            TimeSpan.Zero);
        var restarted = observed.Restart(new("new-epoch", Origin), Now);
        Assert.Empty(restarted.Checkpoint.ActiveNativeIds);
        Assert.IsType<InitialBackupHistoryPosition>(restarted.Checkpoint.Position);
        Assert.IsType<DrainingBackupHistory>(restarted.Mode);
        Assert.True(restarted.Revision > observed.Revision);
    }

    [Fact]
    public void Publisher_or_snapshot_version_changes_cannot_be_accepted_in_the_existing_epoch()
    {
        var initial = BackupArchiveHistory.Begin(new("epoch", Origin), Now);
        var page = initial.Accept(
            new PagedBackupHistoryPosition(new("publisher", "rv"), "next"),
            [],
            Now,
            TimeSpan.Zero);
        Assert.Throws<InvalidOperationException>(() => page.Accept(
            new FollowingBackupHistoryPosition(new("publisher", "different")),
            [],
            Now,
            TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => page.Accept(
            new FollowingBackupHistoryPosition(new("foreign", "rv")),
            [],
            Now,
            TimeSpan.Zero));
        var watching = initial.Accept(
            new FollowingBackupHistoryPosition(new("publisher", "rv")),
            [],
            Now,
            TimeSpan.Zero);
        Assert.Throws<InvalidOperationException>(() => watching.Accept(
            new PagedBackupHistoryPosition(new("publisher", "rv"), "next"),
            [],
            Now,
            TimeSpan.Zero));
    }

    [Fact]
    public void Persisted_monitoring_keeps_its_lease_mode_checkpoint_and_active_identity_set()
    {
        var history = BackupArchiveHistory.Begin(new("epoch", Origin), Now)
            .RequestDrain(Suspended, new("drain", Origin), Now)
            .Accept(
                new PagedBackupHistoryPosition(new("publisher", "opaque"), "next"),
                [new("b", true), new("a", true)],
                Now,
                TimeSpan.Zero)
            .Claim(new("lease", Now.AddMinutes(1)));
        var document = history.ToBsonDocument(typeof(BackupArchiveHistory));
        var restored
            = Assert.IsType<MonitoringBackupHistory>(BsonSerializer.Deserialize<BackupArchiveHistory>(document));
        Assert.Equal(history.Revision, restored.Revision);
        Assert.Equal(history.Checkpoint.Position, restored.Checkpoint.Position);
        Assert.True(history.Checkpoint.ActiveNativeIds.SetEquals(restored.Checkpoint.ActiveNativeIds));
        Assert.Equal(history.Mode, restored.Mode);
        Assert.Equal(history.Dispatch, restored.Dispatch);
        Assert.Equal(Origin, restored.ExecutionOrigin);
    }

    [Fact]
    public void Removed_source_stops_observation_and_cannot_be_drained_or_claimed_again()
    {
        var stopped = BackupArchiveHistory.Begin(new("epoch", Origin), Now).Stop("source-removed");
        Assert.Same(stopped, stopped.Stop("source-removed"));
        Assert.Throws<InvalidOperationException>(() => stopped.RequestDrain(Suspended, new("next", Origin), Now));
        Assert.Throws<InvalidOperationException>(() => stopped.Claim(new("lease", Now.AddMinutes(1))));
    }
}
