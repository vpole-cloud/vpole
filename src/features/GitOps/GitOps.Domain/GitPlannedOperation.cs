using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Domain;

public sealed record GitResourceIdentity(
    string Kind,
    string Name,
    string ResourceId
);

public sealed record GitResourceVersion(
    string DocumentId,
    long Revision
);

public sealed record GitResourcePreconditions(
    GitResourceIdentity Resource,
    string? OwnerSourceId,
    ImmutableArray<GitResourceVersion> Versions,
    string ConfigurationHash
);

public sealed record GitPlanStamp(
    GitRunStamp Run,
    GitResourceIdentity Resource,
    DeclarationPhase Phase
);

public sealed record GitPlanDecision(
    GitPlannedAction Action,
    GitResourcePreconditions? Expected,
    GitManagedConfiguration? Desired,
    ImmutableArray<GitResourceVersion> Dependencies,
    ImmutableArray<string> ChangedFields
);

// Execution results belong exclusively to GitOperationReceipt.
public sealed record GitPlannedOperation(
    [property: BsonId] string Id,
    GitPlanStamp Stamp,
    long Revision,
    GitPlanDecision Decision,
    DateTime EvaluatedAtUtc
);

public static class GitPlannedOperationFactory
{
    public static GitPlannedOperation Create(
        GitPlanStamp stamp,
        GitPlanDecision decision,
        DateTime now)
    {
        GitSourceGuards.Utc(now);
        GitSourceGuards.Required(stamp.Resource.ResourceId, nameof(stamp.Resource.ResourceId));
        var source = stamp.Run.Source;
        var resource = stamp.Resource;
        var id = $"{source.TenantId}/{source.SourceId}/{stamp.Run.RunId}/{resource.Kind}/{resource.Name}/{stamp.Phase}";
        return new GitPlannedOperation(id, stamp, 1, Normalize(decision), now);
    }

    public static GitPlannedOperation Replan(
        GitPlannedOperation previous,
        GitPlanDecision decision,
        DateTime now)
    {
        GitSourceGuards.Utc(now);
        return previous with
        {
            Revision = checked(previous.Revision + 1),
            Decision = Normalize(decision),
            EvaluatedAtUtc = now,
        };
    }

    private static GitPlanDecision Normalize(GitPlanDecision decision)
    {
        if (decision.ChangedFields.IsDefault)
            throw new ArgumentException("Changed fields must be initialized.", nameof(decision));

        return decision with
        {
            ChangedFields = decision.ChangedFields
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray(),
        };
    }
}

// Only provider-approved managed fields; never credentials, Secret values or runtime observations.
public sealed record GitManagedConfiguration(
    string Hash,
    string ProjectionJson
);

public sealed record GitManagedProjections(
    GitManagedConfiguration? Desired,
    GitManagedConfiguration? Applied
);

public static class GitManagedProjectionsFactory
{
    public static GitManagedProjections Empty() => Create(null, null);

    public static GitManagedProjections Create(GitManagedConfiguration? desired, GitManagedConfiguration? applied) =>
        new(desired, applied);
}
