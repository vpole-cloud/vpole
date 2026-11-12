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
        BackupInputRules.ValidStorageLocation(endpoint, "tenant-bucket", "archive"));

    [Fact]
    public void Next_run_follows_the_applied_UTC_schedule_if_operator_status_is_stale()
    {
        var now = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);
        var stale = new BackupObservation("Healthy", null, null, now.AddMinutes(-2).UtcDateTime);
        var policy = new BackupPolicy(
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
            1,
            1,
            null,
            false,
            now.UtcDateTime,
            null,
            null,
            0,
            stale);
        Assert.Equal(now.AddHours(2).AddMinutes(30).UtcDateTime, BackupViews.From(policy, now).NextScheduledAt);
        var reported = now.AddHours(3).UtcDateTime;
        Assert.Equal(
            reported,
            BackupViews.From(policy with { Observation = stale with { NextScheduledAt = reported } }, now)
                .NextScheduledAt);
        Assert.Null(BackupViews.From(policy with { Suspended = true }, now).NextScheduledAt);
        Assert.Null(BackupViews.From(policy with { Deleting = true }, now).NextScheduledAt);
        Assert.Null(BackupViews.From(policy with { AppliedRevision = 0 }, now).NextScheduledAt);
        Assert.Null(BackupViews.From(policy with { Schedule = "0 1 31 2 *" }, now).NextScheduledAt);
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
        var op = BackupOperation.RequestRestore("o", "t", "key", "p", "a", null, time, "restore", "new", null, time);
        Assert.Equal(time, BsonSerializer.Deserialize<BackupOperation>(op.ToBson()).TargetTime);
    }
}
