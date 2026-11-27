// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Architecture;
using Pole.Database;
using Pole.Features.SourceBuilds.Application.Mongo;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Execution;

public sealed record SourceBuildExecutionIdentity(
    string TenantId,
    string BuildJobId,
    string WorkerId,
    long Attempt
);

public delegate Task<bool> RenewSourceBuildExecution(
    SourceBuildExecutionIdentity identity,
    CancellationToken cancellationToken = default);

[ViolateArchitecture(
    ArchitectureRules.RawBson,
    "Build leases calculate expiry with $$NOW in MongoDB; renewal must preserve the atomic computed update.")]
internal static class SourceBuildExecutionLease
{
    internal const int LeaseMilliseconds = 120_000;

    internal static BsonValue Expiry => new BsonDocument("$add", new BsonArray { "$$NOW", LeaseMilliseconds });

    internal static UpdateDefinition<SourceBuildJob> Renewal() => new PipelineUpdateDefinition<SourceBuildJob>(
        new[]
        {
            new BsonDocument(
                "$set",
                new BsonDocument(
                    MongoFields.Field<SourceBuildJob, DateTime>(job =>
                        ((ClaimedSourceBuildExecution)job.Execution).ExpiresAtUtc),
                    Expiry)),
        });
}

internal sealed class MongoRenewSourceBuildExecution(
    IMongoCollection<SourceBuildJob> jobs
)
{
    public async Task<bool> InvokeAsync(
        SourceBuildExecutionIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var result = await jobs.UpdateOneAsync(
            SourceBuildJobFilters.OwnedLease(identity),
            SourceBuildExecutionLease.Renewal(),
            cancellationToken: cancellationToken);
        return result.MatchedCount == 1;
    }
}
