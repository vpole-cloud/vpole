// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.SourceBuilds.Domain;
using Xunit;

namespace Pole.Features.SourceBuilds.Tests;

public sealed class SourceBuildLogTests
{
    [Fact]
    public void Capture_bounds_UTF8_and_entries_preserves_prefix_and_round_trips()
    {
        var now = DateTime.UtcNow;
        var log = Create(now, new SourceBuildLogLimits(4096, 16, 512, 7));
        for (var sequence = 1; sequence <= 16; sequence++)
            log = log.Append(sequence, SourceBuildLogStream.StandardError, new string('x', 255) + "🙂", now);
        Assert.True(log.Truncated);
        Assert.True(log.Entries.Count <= 16);
        Assert.True(log.CapturedBytes <= 4096);
        Assert.Equal(log.CapturedBytes, log.Entries.Sum(entry => Encoding.UTF8.GetByteCount(entry.Text)));
        Assert.DoesNotContain("�", string.Concat(log.Entries.Select(entry => entry.Text)));
        var restored = BsonSerializer.Deserialize<SourceBuildLog>(log.ToBson());
        Assert.Equal(log.LastSequence, restored.LastSequence);
        Assert.Equal(log.CapturedBytes, restored.CapturedBytes);
        Assert.IsType<OpenSourceBuildLog>(restored.State);
        Assert.Same(log, log.Append(log.LastSequence + 1, SourceBuildLogStream.StandardOutput, "ignored", now));
    }

    [Fact]
    public void Duplicate_append_is_idempotent_and_changed_payload_or_sequence_is_rejected()
    {
        var now = DateTime.UtcNow;
        var log = Create(now).Append(1, SourceBuildLogStream.StandardOutput, "first", now);
        Assert.Same(log, log.Append(1, SourceBuildLogStream.StandardOutput, "first", now));
        Assert.Throws<InvalidOperationException>(() => log.Append(
            1,
            SourceBuildLogStream.StandardOutput,
            "changed",
            now));
        Assert.Throws<InvalidOperationException>(() => log.Append(3, SourceBuildLogStream.StandardOutput, "gap", now));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.Append(
            2,
            SourceBuildLogStream.StandardOutput,
            new string('x', 8193),
            now));
    }

    [Fact]
    public void Lease_expiry_fences_late_writes_and_retention_removes_payload_keeps_cursor_tombstone()
    {
        var now = DateTime.UtcNow;
        var log = Create(now).Append(1, SourceBuildLogStream.StandardOutput, "saved", now);
        Assert.Throws<InvalidOperationException>(() => log.Renew(now.AddMinutes(3)));
        var interrupted = log.Recover(now.AddMinutes(3));
        Assert.IsType<InterruptedSourceBuildLog>(interrupted.State);
        Assert.Single(interrupted.Entries);
        var expired = interrupted.Recover(now.AddDays(8));
        Assert.IsType<ExpiredSourceBuildLog>(expired.State);
        Assert.Empty(expired.Entries);
        Assert.Equal(1, expired.LastSequence);
        Assert.Same(expired, expired.Recover(now.AddDays(9)));
        Assert.Throws<InvalidOperationException>(() => expired.Append(
            2,
            SourceBuildLogStream.StandardOutput,
            "late",
            now.AddDays(9)));
    }

    private static SourceBuildLog Create(DateTime now, SourceBuildLogLimits? limits = null) =>
        SourceBuildLogFactory.Create(
            ObjectId.GenerateNewId().ToString(),
            "tenant",
            "service",
            "writer",
            limits ?? new SourceBuildLogLimits(1024 * 1024, 2048, 4096, 7),
            now);
}
