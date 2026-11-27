// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Backups.Domain;
using Xunit;

namespace Pole.Features.Backups.Tests;

public sealed class BackupPolicyDomainTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);
    private static readonly BackupExecutionOrigin Origin = new("origin-trace", "origin-correlation");

    private static BackupPolicy Policy() => BackupPolicyFactory.Create(
        "policy",
        "tenant",
        "daily",
        "pole/postgresql",
        "source",
        "destination",
        "archive",
        "0 2 * * *",
        false,
        30,
        Origin,
        Now);

    [Fact]
    public void Factory_owns_initial_state_and_both_dispatch_variants_round_trip()
    {
        var policy = Policy();
        Assert.Equal(1, policy.Revision);
        Assert.Equal(0, policy.AppliedRevision);
        Assert.IsType<ActiveBackupPolicy>(policy.Lifecycle);
        Assert.Equal(new AvailableBackupDispatch(Now, Origin), policy.Dispatch);
        Assert.Equal(policy, BsonSerializer.Deserialize<BackupPolicy>(policy.ToBson()));

        var claimed = policy.Claim(new BackupLease("worker", Now.AddMinutes(3)));
        var restored = BsonSerializer.Deserialize<BackupPolicy>(claimed.ToBson());
        Assert.Equal(claimed, restored);
        Assert.Equal("worker", Assert.IsType<ClaimedBackupDispatch>(restored.Dispatch).Lease.Id);
        Assert.Equal(Origin, restored.Dispatch.Origin);
    }

    [Fact]
    public void Reconfiguration_preserves_noop_and_releases_old_claim_on_new_revision()
    {
        var claimed = Policy().Claim(new BackupLease("old-worker", Now.AddMinutes(3)));
        Assert.Same(claimed, claimed.Reconfigure("destination", "archive", "0 2 * * *", false, 30, Origin, Now));

        var changed = claimed.Reconfigure("destination", "archive", "0 3 * * *", false, 30, Origin, Now);
        Assert.Equal(2, changed.Revision);
        Assert.IsType<AvailableBackupDispatch>(changed.Dispatch);
        var stopping = changed.BeginStopping(Origin, Now);
        Assert.IsType<StoppingBackupPolicy>(stopping.Lifecycle);
        Assert.Same(stopping, stopping.BeginStopping(Origin, Now));
        Assert.Throws<InvalidOperationException>(() => stopping.Reconfigure(
            "destination",
            "archive",
            "0 4 * * *",
            false,
            30,
            Origin,
            Now));
    }
}
