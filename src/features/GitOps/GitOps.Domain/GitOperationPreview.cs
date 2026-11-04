using Pole.Features.GitOps.Primitives;
using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitOps.Domain;

public sealed record GitPreviewSourceStamp(
    GitSourceIdentity Source,
    long ConfigurationRevision,
    string Commit,
    string DesiredHash);

public sealed record GitPreviewOperation(
    GitResourceIdentity Resource,
    GitPlannedAction Action,
    DeclarationPhase Phase,
    GitResourcePreconditions? Expected);

public sealed record GitPreviewObservation(
    GitResourceIdentity Resource,
    GitResourcePreconditions? Current);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(GitPreviewResourceChanges), typeof(GitPreviewDetachSource), typeof(GitPreviewRelocateSource))]
public closed record GitPreviewIntent;

public sealed record GitPreviewResourceChanges : GitPreviewIntent;

public sealed record GitPreviewDetachSource : GitPreviewIntent;

public sealed record GitPreviewRelocateSource(GitRepositoryLocation Destination) : GitPreviewIntent;

public enum GitPreviewValidity { Current, Stale, AlreadyConfirmed }

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(GitPreviewAwaitingConfirmation), typeof(GitPreviewConfirmed))]
public closed record GitPreviewState;

public sealed record GitPreviewAwaitingConfirmation : GitPreviewState;

public sealed record GitPreviewConfirmed(
    string ConfirmedByUserId,
    DateTime ConfirmedAtUtc) : GitPreviewState;

// All observations are server-read. The HTTP confirmation supplies only preview id and revision.
public sealed record GitPreviewConfirmationContext(
    string PreviewId,
    long PreviewRevision,
    GitPreviewSourceStamp Source,
    ImmutableArray<GitPreviewObservation> Observations,
    string ActorUserId,
    DateTime NowUtc);

public sealed record GitOperationPreview(
    [property: BsonId] string Id,
    long Revision,
    GitPreviewSourceStamp Source,
    GitPreviewIntent Intent,
    ImmutableArray<GitPreviewOperation> Operations,
    GitPreviewState State)
{
    public GitPreviewValidity Evaluate(GitPreviewConfirmationContext context)
    {
        if (context.PreviewId != Id || context.PreviewRevision != Revision || context.Source != Source)
            return GitPreviewValidity.Stale;

        if (context.Observations.IsDefault || context.Observations.Select(item => item.Resource).Distinct().Count() !=
            context.Observations.Length ||
            context.Observations.Length != Operations.Select(item => item.Resource).Distinct().Count())
            return GitPreviewValidity.Stale;

        foreach (var operation in Operations)
        {
            var observation = context.Observations.SingleOrDefault(item => item.Resource == operation.Resource);
            if (observation is null || !Matches(operation.Expected, observation.Current))
                return GitPreviewValidity.Stale;
        }

        return State is GitPreviewConfirmed ? GitPreviewValidity.AlreadyConfirmed : GitPreviewValidity.Current;
    }

    public GitOperationPreview Confirm(GitPreviewConfirmationContext context)
    {
        GitSourceGuards.Required(context.ActorUserId, nameof(context.ActorUserId));
        GitSourceGuards.Utc(context.NowUtc);
        if (Evaluate(context) != GitPreviewValidity.Current)
            throw new InvalidOperationException("Only an unchanged, unconfirmed preview can be confirmed.");

        return this with
        {
            Revision = checked(Revision + 1),
            State = new GitPreviewConfirmed(context.ActorUserId, context.NowUtc)
        };
    }

    private static bool Matches(GitResourcePreconditions? expected, GitResourcePreconditions? current)
    {
        if (expected is null || current is null)
            return expected is null && current is null;

        return expected.Resource == current.Resource && expected.OwnerSourceId == current.OwnerSourceId &&
            expected.ConfigurationHash == current.ConfigurationHash &&
            expected.Versions.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
                .SequenceEqual(current.Versions.OrderBy(item => item.DocumentId, StringComparer.Ordinal));
    }
}

public static class GitOperationPreviewFactory
{
    public static GitOperationPreview Create(
        string id,
        GitPreviewSourceStamp source,
        GitPreviewIntent intent,
        IEnumerable<GitPreviewOperation> operations)
    {
        GitSourceGuards.Required(id, nameof(id));
        GitSourceGuards.Identity(source.Source);
        GitSourceGuards.Required(source.Commit, nameof(source.Commit));
        GitSourceGuards.Required(source.DesiredHash, nameof(source.DesiredHash));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.ConfigurationRevision);
        var accepted = operations.ToImmutableArray();
        ArgumentNullException.ThrowIfNull(intent);
        if (accepted.IsEmpty && intent is GitPreviewResourceChanges ||
            accepted.Select(item => (item.Resource, item.Action, item.Phase)).Distinct().Count()
            != accepted.Length)
            throw new ArgumentException("A preview needs distinct concrete operations.", nameof(operations));

        foreach (var operation in accepted)
        {
            if ((operation.Action == GitPlannedAction.Create) != (operation.Expected is null) ||
                operation.Expected is { } expected && (expected.Resource != operation.Resource ||
                    expected.Versions.IsDefaultOrEmpty || expected.Versions.Any(version => version.Revision <= 0) ||
                    expected.Versions.Select(version => version.DocumentId).Distinct().Count() !=
                    expected.Versions.Length))
                throw new ArgumentException("An operation requires exact identity and existence preconditions.");
        }

        return new(id, 1, source, intent, accepted, new GitPreviewAwaitingConfirmation());
    }
}
