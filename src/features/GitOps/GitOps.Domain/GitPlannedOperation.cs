using Pole.Features.GitOps.Primitives;
using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitOps.Domain;

public sealed record GitResourceIdentity(
    string Kind,
    string Name,
    string ResourceId);

public sealed record GitResourceVersion(
    string DocumentId,
    long Revision);

public sealed record GitResourcePreconditions(
    GitResourceIdentity Resource,
    string? OwnerSourceId,
    ImmutableArray<GitResourceVersion> Versions,
    string ConfigurationHash);

public enum GitPlannedAction { Create, Update, NoOp, Adopt, Replace, Remove, Detach }

public sealed record GitOperationDiagnostic(
    string Field,
    string Code);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(GitOperationPending), typeof(GitOperationSucceeded), typeof(GitOperationBlocked))]
public closed record GitPlannedOperationState;

public sealed record GitOperationPending : GitPlannedOperationState;

public sealed record GitOperationSucceeded : GitPlannedOperationState;

public sealed record GitOperationBlocked(GitOperationDiagnostic Diagnostic) : GitPlannedOperationState;

public sealed record GitPlannedOperation(
    [property: BsonId] string Id,
    GitRunStamp Run,
    GitResourceIdentity Resource,
    GitPlannedAction Action,
    DeclarationPhase Phase,
    GitResourcePreconditions? Expected,
    string DesiredHash,
    GitPlannedOperationState State);

// Projection JSON contains only provider-approved managed fields, never secret values or runtime observations.
public sealed record GitManagedConfiguration(
    string Hash,
    string ProjectionJson);

public sealed record GitManagedResourceState(
    [property: BsonId] string Id,
    GitSourceIdentity Owner,
    GitResourceIdentity Resource,
    GitManagedConfiguration Desired,
    GitManagedConfiguration? Applied,
    bool PendingRemoval)
{
    public GitManagedResourceState RetainRemoved() => this with { PendingRemoval = true };
}
