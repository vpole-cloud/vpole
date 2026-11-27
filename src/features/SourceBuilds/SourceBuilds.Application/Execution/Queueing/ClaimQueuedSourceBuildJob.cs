// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Architecture;
using Pole.Database;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Mongo;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Execution.Queueing;

public sealed record ClaimSourceBuildJobRequest(
    string BuildJobId,
    string WorkerId
);

public delegate Task<SourceBuildJob?> ClaimQueuedSourceBuildJob(
    ClaimSourceBuildJobRequest request,
    CancellationToken cancellationToken = default);

internal sealed record SourceBuildJobNotClaimed;

internal union ClaimSourceBuildJobOutcome(SourceBuildJob, SourceBuildJobNotClaimed);

internal sealed class MongoClaimQueuedSourceBuildJob(
    IMongoCollection<SourceBuildJob> jobs,
    MongoRunTransaction transactions,
    StoreOutboxWrites storeOutboxWrites
)
{
    public async Task<SourceBuildJob?> InvokeAsync(
        ClaimSourceBuildJobRequest request,
        CancellationToken cancellationToken = default)
    {
        // The internal dispatcher supplies job IDs; the claimed document owns tenant context.
        var outcome = await transactions.InvokeOutcomeAsync<ClaimSourceBuildJobOutcome>(
            async (transaction, ct) =>
            {
                var claimed = await jobs.FindOneAndUpdateAsync(
                    transaction.Session,
                    SourceBuildJobFilters.ById(request.BuildJobId) & SourceBuildJobFilters.Recoverable,
                    ClaimTransition(request),
                    new FindOneAndUpdateOptions<SourceBuildJob> { ReturnDocument = ReturnDocument.After },
                    ct);
                if (claimed is null)
                {
                    ClaimSourceBuildJobOutcome rejected = new SourceBuildJobNotClaimed();
                    return transaction.Commit(rejected);
                }

                await storeOutboxWrites(transaction.Session, [OutboxWrite.Create(claimed.ToStateChangedEvent())], ct);
                ClaimSourceBuildJobOutcome accepted = claimed;
                return transaction.Commit(accepted);
            },
            cancellationToken);
        return outcome switch
        {
            SourceBuildJob claimed => claimed,
            SourceBuildJobNotClaimed => null,
        };
    }

    [ViolateArchitecture(
        ArchitectureRules.RawBson,
        "Atomic build claims replace the state discriminator, increment the attempt and set lease " +
        "timestamps from $$NOW.")]
    private static UpdateDefinition<SourceBuildJob> ClaimTransition(ClaimSourceBuildJobRequest request) =>
        new PipelineUpdateDefinition<SourceBuildJob>(
            new[]
            {
                new BsonDocument("$unset", MongoFields.Field<SourceBuildJob, SourceBuildJobState>(job => job.State)),
                new BsonDocument(
                    "$set",
                    new BsonDocument
                    {
                        [MongoFields.Field<SourceBuildJob, SourceBuildExecution>(job => job.Execution)]
                            = new BsonDocument
                            {
                                ["_t"] = new BsonArray
                                {
                                    nameof(SourceBuildExecution), nameof(ClaimedSourceBuildExecution),
                                },
                                [MongoFields.Field<ClaimedSourceBuildExecution, string>(lease => lease.WorkerId)]
                                    = new BsonDocument("$literal", request.WorkerId),
                                [MongoFields.Field<ClaimedSourceBuildExecution, long>(lease => lease.Attempt)]
                                    = new BsonDocument(
                                        "$add",
                                        new BsonArray
                                        {
                                            MongoFields.Reference<SourceBuildJob, long>(job =>
                                                ((ClaimedSourceBuildExecution)job.Execution).Attempt),
                                            1L,
                                        }),
                                [MongoFields.Field<ClaimedSourceBuildExecution, DateTime>(lease => lease.ExpiresAtUtc)]
                                    = SourceBuildExecutionLease.Expiry,
                            },
                        [MongoFields.Field<SourceBuildJob, SourceBuildJobState>(job => job.State)] = new BsonDocument
                        {
                            ["_t"] = new BsonArray { nameof(SourceBuildJobState), nameof(CloningSourceBuild) },
                            [MongoFields.Field<CloningSourceBuild, DateTime>(state => state.StartedAtUtc)] = "$$NOW",
                        },
                        [MongoFields.Field<SourceBuildJob, DateTime>(job => job.UpdatedAtUtc)] = "$$NOW",
                    }),
            });
}
