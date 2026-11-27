// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.GitOps.Domain;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class GitLifecycleCleanupProofTests
{
    [Fact]
    public void Cleanup_advances_only_a_complete_exact_chain_and_can_resume_from_a_later_receipt()
    {
        var proofs = new[] { Proof(8, 9), Proof(7, 8), Proof(8, 9) };
        Assert.Equal(
            9,
            Assert.Single(GitLifecycleCleanupAdvancement.Advance([new GitResourceVersion("parent", 7)], proofs))
                .Revision);
        Assert.Equal(
            9,
            Assert.Single(GitLifecycleCleanupAdvancement.Advance([new GitResourceVersion("parent", 8)], proofs))
                .Revision);
        Assert.Empty(GitLifecycleCleanupAdvancement.Advance([new GitResourceVersion("parent", 9)], proofs));
    }

    [Fact]
    public void An_unproven_external_increment_is_never_bridged()
    {
        var proofs = new[] { Proof(7, 8), Proof(9, 10) };
        Assert.Equal(
            8,
            Assert.Single(GitLifecycleCleanupAdvancement.Advance([new GitResourceVersion("parent", 7)], proofs))
                .Revision);
        Assert.Empty(GitLifecycleCleanupAdvancement.Advance([new GitResourceVersion("other", 7)], proofs));
        Assert.Empty(
            GitLifecycleCleanupAdvancement.Advance(
                [new GitResourceVersion("parent", 7), new GitResourceVersion("parent", 9)],
                proofs));
        var invalid = Proof(7, 8) with { Transitions = [new GitCleanupDocumentTransition("parent", 7, 9)] };
        Assert.Empty(GitLifecycleCleanupAdvancement.Advance([new GitResourceVersion("parent", 7)], [invalid]));
    }

    [Fact]
    public void Proof_requires_a_successful_exact_begin_receipt_and_round_trips_current_bson()
    {
        var receipt = Receipt();
        var proof = GitLifecycleCleanupProofFactory.Create(
            receipt,
            "cleanup",
            [new GitCleanupDocumentTransition("parent", 7, 8)]);
        var loaded = BsonSerializer.Deserialize<GitLifecycleCleanupProof>(proof.ToBson());
        Assert.Equal(proof.ToBsonDocument(), loaded.ToBsonDocument());
        Assert.Equal(receipt.Id, loaded.BeginReceiptId);
        Assert.Throws<ArgumentException>(() => GitLifecycleCleanupProofFactory.Create(
            receipt with
            {
                Blocker = "blocked",
            },
            "cleanup",
            [new GitCleanupDocumentTransition("parent", 7, 8)]));
        Assert.Throws<ArgumentException>(() => GitLifecycleCleanupProofFactory.Create(
            receipt,
            "cleanup",
            [new GitCleanupDocumentTransition("parent", 7, 9)]));
    }

    private static GitLifecycleCleanupProof Proof(long before, long after) => GitLifecycleCleanupProofFactory.Create(
        Receipt(),
        $"cleanup-{before}",
        [new GitCleanupDocumentTransition("parent", before, after)]);

    private static GitOperationReceipt Receipt() => new(
        "receipt",
        "tenant",
        "source",
        "run",
        1,
        "Widget",
        "demo",
        "lifecycle",
        "resource",
        "desired",
        null,
        true,
        1,
        new GitLifecycleEffect(
            1,
            "action",
            GitLifecycleStep.BeginRemoval,
            null,
            0,
            ImmutableArray<GitResourceVersion>.Empty,
            new GitResourceIdentity("Widget", "demo", "resource")));
}
