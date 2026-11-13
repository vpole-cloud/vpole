using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Endpoints;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Xunit;

namespace Pole.Features.Backups.Tests;

public sealed class BackupBoundaryTests
{
    [Theory]
    [InlineData("https://example.test", true)]
    [InlineData("http://localhost:9000", true)]
    [InlineData("https://user@evil.test", false)]
    [InlineData("ftp://private", false)]
    [InlineData("https://storage.test/buckets?key=unsafe", false)]
    public void Destination_accepts_only_an_explicit_storage_origin(string endpoint, bool expected) => Assert.Equal(
        expected,
        BackupInputRules.ValidEndpoint(endpoint));

    [Fact]
    public void Next_run_follows_the_applied_UTC_schedule_if_operator_status_is_stale()
    {
        var now = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);
        var stale = new BackupObservation("Healthy", null, null, now.AddMinutes(-2).UtcDateTime);
        var origin = new BackupExecutionOrigin("trace", "correlation");
        var policy = BackupPolicyFactory.Create(
                "p",
                "t",
                "daily",
                "pole/postgres",
                "s",
                "d",
                "a",
                "30 2 * * *",
                false,
                30,
                origin,
                now.UtcDateTime) with
            {
                AppliedRevision = 1, Observation = stale
            };
        Assert.Equal(now.AddHours(2).AddMinutes(30).UtcDateTime, policy.ReportSchedule(now).NextScheduledAt);
        var reported = now.AddHours(3).UtcDateTime;
        Assert.Equal(
            reported,
            (policy with { Observation = stale with { NextScheduledAt = reported } }).ReportSchedule(now)
            .NextScheduledAt);
        Assert.Null((policy with { Suspended = true }).ReportSchedule(now).NextScheduledAt);
        Assert.Null(policy.BeginStopping(origin, now.UtcDateTime).ReportSchedule(now).NextScheduledAt);
        Assert.Null((policy with { AppliedRevision = 0 }).ReportSchedule(now).NextScheduledAt);
        Assert.Null((policy with { Schedule = "0 1 31 2 *" }).ReportSchedule(now).NextScheduledAt);
    }

    [Fact]
    public void Sensitive_catalog_credentials_are_redacted() => Assert.DoesNotContain(
        "sensitive-value",
        new BackupCatalogCredentials("id", "sensitive-value").ToString());

    [Fact]
    public void PITR_rejects_unzoned_times_and_preserves_submillisecond_precision_in_durable_commands()
    {
        Assert.False(BackupHttpProblems.TryTime("2026-04-01T12:22:00", out _));
        Assert.False(BackupHttpProblems.TryTime("2026-04-01T12:22:00.1234567Z", out _));
        Assert.True(BackupHttpProblems.TryTime("2026-04-01T12:22:00.1234560Z", out _));
        Assert.True(BackupHttpProblems.TryTime("2026-04-01T12:22:00.000001+03:00", out var timestamp));
        var time = timestamp.UtcDateTime;
        Assert.Equal(9, time.Hour);
        var op = BackupOperation.RequestRestore(
            "o",
            "t",
            "key",
            "p",
            "a",
            new BackupTimeSelection(time),
            new BackupRestoreResource("new", "restore", null),
            time,
            new BackupExecutionOrigin("trace", "correlation"));
        Assert.Equal(
            time,
            Assert.IsType<BackupTimeSelection>(
                    Assert.IsType<RestoreBackupWork>(BsonSerializer.Deserialize<BackupOperation>(op.ToBson()).Work)
                        .Selection)
                .TargetTimeUtc);
    }
}
