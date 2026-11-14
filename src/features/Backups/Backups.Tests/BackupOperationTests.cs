using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Backups.Domain;
using Xunit;

namespace Pole.Features.Backups.Tests;

public sealed class BackupOperationTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly BackupExecutionOrigin Origin = new("trace", "correlation");

    [Fact]
    public void Copy_success_requires_an_identity_and_terminal_operations_reject_dispatch()
    {
        var requested = Copy();
        Assert.Throws<ArgumentException>(() => requested.CompleteCopy("", Now));
        var completed = RoundTrip(requested.CopyRunning(Now, TimeSpan.Zero).CompleteCopy("backup-id", Now));
        Assert.Equal(
            "backup-id",
            Assert.IsType<CompletedBackupCopy>(Assert.IsType<ManualBackupWork>(completed.Work).Execution).BackupId);
        Assert.Null(completed.Dispatch);
        Assert.Throws<InvalidOperationException>(() => completed.Claim(new BackupLease("claim", Now.AddMinutes(1))));
        Assert.Throws<InvalidOperationException>(() => completed.Fail("late-failure", Now));
        Assert.Throws<InvalidOperationException>(() => completed.Retry(Now, TimeSpan.Zero, null));
    }

    [Fact]
    public void Restore_receipt_is_a_durable_state_and_success_requires_the_target()
    {
        var requested = Restore();
        Assert.Throws<InvalidOperationException>(() => requested.CompleteRestore(Now));
        Assert.Throws<InvalidOperationException>(() => requested.RecordRestoreTarget("another-resource", Now));
        var claimed = requested.Claim(new BackupLease("lease", Now.AddMinutes(1)));
        var provisioning = RoundTrip(claimed.RecordRestoreTarget("new-resource", Now));
        Assert.Equal(BackupOperationState.Running, provisioning.State);
        Assert.Equal(Origin, provisioning.Dispatch!.Origin);
        Assert.Equal("lease", Assert.IsType<ClaimedBackupDispatch>(provisioning.Dispatch).Lease.Id);
        var retried = RoundTrip(provisioning.Retry(Now, TimeSpan.FromSeconds(30), "temporarily-unavailable"));
        Assert.Equal(
            "new-resource",
            Assert.IsType<ProvisioningBackupRestore>(Assert.IsType<RestoreBackupWork>(retried.Work).Execution)
                .Target.ResourceId);
        var complete = RoundTrip(retried.CompleteRestore(Now));
        Assert.Equal(Origin, complete.Origin);
        Assert.Equal(
            "new-resource",
            Assert.IsType<CompletedBackupRestore>(Assert.IsType<RestoreBackupWork>(complete.Work).Execution)
                .Target.ResourceId);
        Assert.Throws<InvalidOperationException>(() => complete.CompleteRestore(Now));
        Assert.Throws<InvalidOperationException>(() => complete.Claim(new BackupLease("lease", Now.AddMinutes(1))));
    }

    [Fact]
    public void Restore_failure_preserves_whether_creation_was_committed()
    {
        var notCreated = RoundTrip(Restore().Fail("catalog-unavailable", Now));
        Assert.IsType<AwaitingRestoreTarget>(
            Assert.IsType<FailedBackupRestore>(Assert.IsType<RestoreBackupWork>(notCreated.Work).Execution).Target);
        var created = RoundTrip(Restore().RecordRestoreTarget("new-resource", Now).Fail("recovery-failed", Now));
        Assert.Equal(
            "new-resource",
            Assert.IsType<RecordedRestoreTarget>(
                    Assert.IsType<FailedBackupRestore>(Assert.IsType<RestoreBackupWork>(created.Work).Execution).Target)
                .ResourceId);
        Assert.Throws<InvalidOperationException>(() => created.Retry(Now, TimeSpan.Zero, null));
    }

    [Fact]
    public void Scheduled_observation_has_no_dispatch_and_interruption_is_terminal()
    {
        var observed = BackupOperation.ObserveScheduled(
            "id",
            "tenant",
            "key",
            "policy",
            "archive",
            "native",
            2,
            "scan",
            new RunningScheduledBackup(),
            Now,
            Now,
            Origin);
        var interrupted = RoundTrip(observed.InterruptObservation("source-removed", Now));
        Assert.Equal(BackupOperationState.Interrupted, interrupted.State);
        Assert.Equal("source-removed", interrupted.FailureCode);
        Assert.Throws<InvalidOperationException>(() => interrupted.InterruptObservation("again", Now));
        Assert.Throws<InvalidOperationException>(() => observed.Claim(new BackupLease("lease", Now.AddMinutes(1))));
    }

    private static BackupOperation Copy() => BackupOperation.RequestCopy(
        "id",
        "tenant",
        "key",
        "policy",
        "archive",
        Now,
        Origin);

    private static BackupOperation Restore() => BackupOperation.RequestRestore(
        "id",
        "tenant",
        "key",
        "policy",
        "archive",
        new BackupTimeSelection(Now.AddTicks(10)),
        new BackupRestoreResource("new-resource", "restored", 20),
        Now,
        Origin);

    private static BackupOperation RoundTrip(BackupOperation operation) =>
        BsonSerializer.Deserialize<BackupOperation>(operation.ToBson());
}
