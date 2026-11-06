using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.GitOps.Domain;

public sealed record GitLifecycleCleanupProof(
    [property: BsonId] string Id,
    string TenantId,
    string SourceId,
    string RunId,
    string BeginReceiptId,
    string ActionId,
    GitResourceIdentity Subject,
    string CleanupId,
    ImmutableArray<GitCleanupDocumentTransition> Transitions) : ITenantOwned;

public sealed record GitCleanupDocumentTransition(
    string DocumentId,
    long Before,
    long After);

public static class GitLifecycleCleanupProofFactory
{
    public static GitLifecycleCleanupProof Create(GitOperationReceipt receipt, string cleanupId,
        IEnumerable<GitCleanupDocumentTransition> transitions)
    {
        if (receipt.Blocker is not null || receipt.Lifecycle is not { Step: GitLifecycleStep.BeginRemoval } effect ||
            effect.Subject.ResourceId != receipt.ResourceId || effect.Subject.Kind != receipt.Kind)
            throw new ArgumentException("Cleanup proof requires an exact successful removal request.", nameof(receipt));
        ArgumentException.ThrowIfNullOrWhiteSpace(cleanupId);
        var changes = transitions.ToImmutableArray();
        if (changes.IsDefaultOrEmpty || changes.Any(item => string.IsNullOrWhiteSpace(item.DocumentId) ||
                item.Before < 0 || item.After != checked(item.Before + 1)) ||
            changes.Select(item => item.DocumentId).Distinct(StringComparer.Ordinal).Count() != changes.Length)
            throw new ArgumentException("Cleanup proof requires distinct exact fence increments.", nameof(transitions));

        return new($"{receipt.Id}/cleanup/{cleanupId}", receipt.TenantId, receipt.SourceId, receipt.RunId,
            receipt.Id, effect.ActionId, effect.Subject, cleanupId, changes);
    }
}

public static class GitLifecycleCleanupAdvancement
{
    public static ImmutableArray<GitResourceVersion> Advance(IEnumerable<GitResourceVersion> baseline,
        IEnumerable<GitLifecycleCleanupProof> proofs)
    {
        var transitions = proofs.SelectMany(item => item.Transitions)
            .Where(item => item.Before >= 0 && item.Before < long.MaxValue && item.After == item.Before + 1)
            .Distinct().ToArray();
        var result = ImmutableArray.CreateBuilder<GitResourceVersion>();
        foreach (var documents in baseline.GroupBy(item => item.DocumentId, StringComparer.Ordinal))
        {
            // Inconsistent approved baselines must remain inconsistent, so ordinary validation rejects them.
            var values = documents.Select(item => item.Revision).Distinct().ToArray();
            if (values.Length != 1)
                continue;
            var current = values[0];
            while (transitions.Where(item => item.DocumentId == documents.Key && item.Before == current)
                       .Distinct().ToArray() is [var next])
                current = next.After;
            if (current != values[0])
                result.Add(new(documents.Key, current));
        }
        return result.ToImmutable();
    }
}
