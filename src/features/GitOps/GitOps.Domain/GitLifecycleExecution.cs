using System.Collections.Immutable;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Domain;

public enum GitLifecycleStep { Adopt, BeginRemoval, RemovalCompleted, Detach, Retain, Apply }

public sealed record GitLifecycleExecutionPlan(
    string PreviewId,
    long PreviewRevision,
    GitPreviewSourceStamp Source,
    GitPreviewIntent Intent,
    ImmutableArray<GitPreviewOperation> Operations,
    ImmutableArray<GitPreviewPinnedReference> References,
    ImmutableArray<GitPreviewDependencySet> DependencySets,
    GitPreviewState Authorization
);

// Only receipt-proven postconditions may advance the versions approved in the preview.
public sealed record GitLifecycleEffect(
    long Sequence,
    string ActionId,
    GitLifecycleStep Step,
    GitResourcePreconditions? Postcondition,
    long InventoryRevision,
    ImmutableArray<GitResourceVersion> RelatedVersions,
    GitResourceIdentity Subject
);

public static class GitLifecycleExecutionPlanFactory
{
    public static GitLifecycleExecutionPlan Create(GitOperationPreview preview)
    {
        if (preview.State is not (GitPreviewConfirmed or GitPreviewPolicyAuthorized))
            throw new InvalidOperationException("Execution requires a confirmed server-owned preview.");

        return new GitLifecycleExecutionPlan(
            preview.Id,
            preview.Revision,
            preview.Source,
            preview.Intent,
            preview.Operations,
            preview.References,
            preview.DependencySets,
            preview.State);
    }
}

public sealed record GitPreviewPinnedReference(
    string ConsumerResourceId,
    GitResourceIdentity Target,
    ImmutableArray<GitResourceVersion> Versions,
    GitPreviewReferenceState State
);

public sealed record GitPreviewDependency(
    GitResourceIdentity Dependent,
    ImmutableArray<GitResourceVersion> Versions,
    GitLifecycleDependencyOrder Order
);

public sealed record GitPreviewDependencySet(
    string Owner,
    GitResourceIdentity Target,
    ImmutableArray<GitPreviewDependency> Dependencies
);
