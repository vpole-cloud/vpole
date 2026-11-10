using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Domain;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(GitPreviewInventoryInput), typeof(GitPreviewRepositoryInput))]
public closed record GitPreviewInput;

public sealed record GitPreviewInventoryInput : GitPreviewInput;

public sealed record GitPreviewRepositoryInput(
    GitRepositoryLocation Repository,
    GitAcceptedSnapshot Snapshot
) : GitPreviewInput;

public sealed record GitPreviewSourceStamp(
    GitSourceIdentity Source,
    long ConfigurationRevision,
    long InventoryRevision,
    GitPreviewInput Input
);

public sealed record GitPreviewOperation(
    string Id,
    GitResourceIdentity Resource,
    GitLifecycleAction Action,
    GitResourcePreconditions? Expected,
    GitResourceIdentity? Replacement,
    ImmutableArray<string> Dependencies,
    ImmutableArray<string> ChangedFields,
    ImmutableArray<string> Warnings,
    GitLifecycleProtection Protection
);

public sealed record GitPreviewObservation(
    GitResourceIdentity Resource,
    GitResourcePreconditions? Current
);

public sealed record GitReplacementSelection(
    GitResourceIdentity Original,
    string DesiredKind,
    string DesiredName
);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(GitPreviewResourceChanges),
    typeof(GitPreviewAdoption),
    typeof(GitPreviewRemoval),
    typeof(GitPreviewReplacement),
    typeof(GitPreviewDetachSource),
    typeof(GitPreviewRelocateSource))]
public closed record GitPreviewIntent;

public sealed record GitPreviewResourceChanges(
    GitPreviewRemovalDisposition Removal
) : GitPreviewIntent;

public sealed record GitPreviewAdoption(
    ImmutableArray<GitResourceIdentity> Resources
) : GitPreviewIntent;

public sealed record GitPreviewRemoval(
    ImmutableArray<GitResourceIdentity> Resources
) : GitPreviewIntent;

public sealed record GitPreviewReplacement(
    ImmutableArray<GitReplacementSelection> Resources
) : GitPreviewIntent;

public sealed record GitPreviewDetachSource : GitPreviewIntent;

public sealed record GitPreviewRelocateSource(
    GitRepositoryLocation Destination
) : GitPreviewIntent;

public enum GitPreviewValidity { Current, Stale, AlreadyConfirmed }

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(GitPreviewAwaitingConfirmation),
    typeof(GitPreviewConfirmed),
    typeof(GitPreviewPolicyAuthorized))]
public closed record GitPreviewState;

public sealed record GitPreviewAwaitingConfirmation : GitPreviewState;

public sealed record GitPreviewConfirmed(
    string ConfirmedByUserId,
    DateTime ConfirmedAtUtc,
    string RunId
) : GitPreviewState;

public sealed record GitPreviewPolicyAuthorized(
    long ConfigurationRevision,
    DateTime AuthorizedAtUtc,
    string RunId
) : GitPreviewState;

public sealed record GitPreviewPolicyAuthorizationContext(
    long ConfigurationRevision,
    DateTime NowUtc,
    string RunId
);

public sealed record GitPreviewConfirmationContext(
    string PreviewId,
    long PreviewRevision,
    GitPreviewSourceStamp Source,
    ImmutableArray<GitPreviewObservation> Observations,
    string ActorUserId,
    DateTime NowUtc,
    string RunId
);

public sealed record GitOperationPreview(
    [property: BsonId] string Id,
    long Revision,
    GitPreviewSourceStamp Source,
    GitPreviewIntent Intent,
    ImmutableArray<GitPreviewOperation> Operations,
    GitPreviewState State,
    DateTime CreatedAtUtc,
    ImmutableArray<GitPreviewPinnedReference> References,
    ImmutableArray<GitPreviewDependencySet> DependencySets
)
{
    public GitPreviewValidity Evaluate(GitPreviewConfirmationContext context)
    {
        if (context.PreviewId != Id || context.PreviewRevision != Revision || !MatchesSource(context.Source))
            return GitPreviewValidity.Stale;

        if (context.Observations.IsDefault ||
            context.Observations.Select(item => item.Resource).Distinct().Count() != context.Observations.Length ||
            context.Observations.Length != Operations.Select(item => item.Resource).Distinct().Count())
            return GitPreviewValidity.Stale;

        foreach (var operation in Operations)
        {
            var observation = context.Observations.SingleOrDefault(item => item.Resource == operation.Resource);
            if (observation is null || !Matches(operation.Expected, observation.Current))
                return GitPreviewValidity.Stale;
        }

        return State is GitPreviewAwaitingConfirmation
            ? GitPreviewValidity.Current
            : GitPreviewValidity.AlreadyConfirmed;
    }

    public GitOperationPreview Confirm(GitPreviewConfirmationContext context)
    {
        GitSourceGuards.Required(context.ActorUserId, nameof(context.ActorUserId));
        GitSourceGuards.Required(context.RunId, nameof(context.RunId));
        GitSourceGuards.Utc(context.NowUtc);
        if (Evaluate(context) != GitPreviewValidity.Current)
            throw new InvalidOperationException("Only an unchanged, unconfirmed preview can be confirmed.");

        return this with
        {
            Revision = checked(Revision + 1),
            State = new GitPreviewConfirmed(context.ActorUserId, context.NowUtc, context.RunId),
        };
    }

    public bool CanAuthorizePrune() => State is GitPreviewAwaitingConfirmation &&
        Intent is GitPreviewResourceChanges { Removal: GitPreviewRemovalDisposition.Remove } &&
        Source.Input is GitPreviewRepositoryInput { Snapshot.Resources.IsEmpty: false } &&
        Operations.Any(item => item.Action == GitLifecycleAction.Remove) &&
        Operations.All(item => item.Action is GitLifecycleAction.Apply or GitLifecycleAction.Remove &&
            (item.Action != GitLifecycleAction.Remove || (item.Protection == GitLifecycleProtection.ConfigurationOnly &&
                item.Expected is not null))) && !Operations.Any(removal =>
            removal.Action == GitLifecycleAction.Remove && Operations.Any(create =>
                create.Action == GitLifecycleAction.Apply && create.Expected is null &&
                create.Resource.Kind == removal.Resource.Kind));

    public GitOperationPreview AuthorizePrune(GitPreviewPolicyAuthorizationContext context)
    {
        GitSourceGuards.Required(context.RunId, nameof(context.RunId));
        GitSourceGuards.Utc(context.NowUtc);
        if (context.ConfigurationRevision != Source.ConfigurationRevision || !CanAuthorizePrune())
            throw new InvalidOperationException("Prune policy cannot authorize this exact server-owned plan.");

        return this with
        {
            Revision = checked(Revision + 1),
            State = new GitPreviewPolicyAuthorized(context.ConfigurationRevision, context.NowUtc, context.RunId),
        };
    }

    private bool MatchesSource(GitPreviewSourceStamp current) => current.Source == Source.Source &&
        current.ConfigurationRevision == Source.ConfigurationRevision &&
        current.InventoryRevision == Source.InventoryRevision && (Source.Input, current.Input) switch
        {
            (GitPreviewInventoryInput, GitPreviewInventoryInput) => true,
            (GitPreviewRepositoryInput expected, GitPreviewRepositoryInput actual) =>
                expected.Repository == actual.Repository && expected.Snapshot.Commit == actual.Snapshot.Commit &&
                expected.Snapshot.DesiredHash == actual.Snapshot.DesiredHash,
            (GitPreviewInventoryInput, GitPreviewRepositoryInput)
                or (GitPreviewRepositoryInput, GitPreviewInventoryInput) => false,
        };

    private static bool Matches(GitResourcePreconditions? expected, GitResourcePreconditions? current)
    {
        if (expected is null || current is null)
            return expected is null && current is null;

        return expected.Resource == current.Resource && expected.OwnerSourceId == current.OwnerSourceId &&
            expected.ConfigurationHash == current.ConfigurationHash && expected.Versions
                .OrderBy(item => item.DocumentId, StringComparer.Ordinal)
                .SequenceEqual(current.Versions.OrderBy(item => item.DocumentId, StringComparer.Ordinal));
    }
}

public static class GitOperationPreviewFactory
{
    public static GitOperationPreview Create(
        string id,
        GitPreviewSourceStamp source,
        GitPreviewIntent intent,
        IEnumerable<GitPreviewOperation> operations,
        DateTime now,
        IEnumerable<GitPreviewPinnedReference>? references = null,
        IEnumerable<GitPreviewDependencySet>? dependencySets = null)
    {
        GitSourceGuards.Required(id, nameof(id));
        GitSourceGuards.Identity(source.Source);
        GitSourceGuards.Utc(now);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.ConfigurationRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(source.InventoryRevision);
        if (source.Input is GitPreviewRepositoryInput repository)
        {
            GitSourceGuards.Required(repository.Snapshot.Commit, nameof(repository.Snapshot.Commit));
            GitSourceGuards.Required(repository.Snapshot.DesiredHash, nameof(repository.Snapshot.DesiredHash));
        }
        else if (intent is not GitPreviewDetachSource)
        {
            throw new ArgumentException("Only source detach uses an inventory-only preview.", nameof(source));
        }

        if (intent is GitPreviewResourceChanges changes && !Enum.IsDefined(changes.Removal))
            throw new ArgumentOutOfRangeException(nameof(intent), "A known removal disposition is required.");

        var accepted = operations.ToImmutableArray();
        if ((accepted.IsEmpty && intent is not GitPreviewDetachSource and not GitPreviewRelocateSource) ||
            accepted.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != accepted.Length)
            throw new ArgumentException("A preview needs distinct concrete operations.", nameof(operations));

        foreach (var operation in accepted)
        {
            GitSourceGuards.Required(operation.Id, nameof(operation.Id));
            if (operation.Dependencies.IsDefault || operation.ChangedFields.IsDefault || operation.Warnings.IsDefault ||
                (operation.Action == GitLifecycleAction.Adopt && operation.Expected is null) ||
                operation.Action == GitLifecycleAction.Replace != operation.Replacement is not null ||
                (operation.Replacement is { } replacement && (replacement.Kind != operation.Resource.Kind ||
                    replacement.ResourceId == operation.Resource.ResourceId)) || (operation.Expected is { } expected &&
                    (expected.Resource != operation.Resource || expected.Versions.IsDefaultOrEmpty ||
                        expected.Versions.Any(version => version.Revision < 0) ||
                        expected.Versions.Select(version => version.DocumentId).Distinct().Count() !=
                        expected.Versions.Length)))
                throw new ArgumentException("An operation requires exact identity and existence preconditions.");
        }

        ValidateGraph(accepted);
        var acceptedDependencies = dependencySets?.ToImmutableArray() ?? [];
        if (!GitLifecycleRemovalGraph.IsAcyclic(accepted, acceptedDependencies))
            throw new ArgumentException("Removal request and finalization dependencies cannot contain a cycle.");
        return new GitOperationPreview(
            id,
            1,
            source,
            intent,
            accepted,
            new GitPreviewAwaitingConfirmation(),
            now,
            references?.ToImmutableArray() ?? [],
            acceptedDependencies);
    }

    private static void ValidateGraph(ImmutableArray<GitPreviewOperation> operations)
    {
        var remaining = operations.ToDictionary(
            item => item.Id,
            item => item.Dependencies.ToHashSet(),
            StringComparer.Ordinal);
        if (remaining.Values.Any(dependencies => dependencies.Any(id => !remaining.ContainsKey(id))))
            throw new ArgumentException("Every lifecycle dependency must belong to the reviewed graph.");

        while (remaining.Count > 0)
        {
            var ready = remaining.Where(item => item.Value.Count == 0).Select(item => item.Key).ToArray();
            if (ready.Length == 0)
                throw new ArgumentException("A lifecycle action graph cannot contain a cycle.");

            foreach (var id in ready)
                remaining.Remove(id);
            foreach (var dependencies in remaining.Values)
                dependencies.ExceptWith(ready);
        }
    }
}
