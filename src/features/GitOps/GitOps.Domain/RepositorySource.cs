using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Domain;

public sealed record GitSourceIdentity(
    string TenantId,
    string SourceId);

public sealed record GitRepositoryLocation(
    string Provider,
    string ConnectionId,
    string ExternalRepositoryId,
    string Branch,
    string ManifestDirectory);

public enum GitSourceActivity { Active, Paused, Detaching, Detached, Deleted }

public enum GitPrunePolicy { Retain, Prune }

public sealed record GitSyncPolicy(TimeSpan PollInterval);

// The creator is audit metadata, never the authority of an automatic synchronization.
public sealed record GitSourceAudit(
    string CreatedByUserId,
    DateTime CreatedAtUtc);

public sealed record RepositorySource(
    [property: BsonId] string Id,
    string TenantId,
    GitRepositoryLocation Repository,
    long Revision,
    GitSyncPolicy Sync,
    GitSourceActivity Activity,
    GitPrunePolicy Prune,
    GitSourceAudit Audit,
    string CreationKey,
    string CreationHash,
    string DisplayUrl)
{
    public RepositorySource Pause() => Activity switch
    {
        GitSourceActivity.Active or GitSourceActivity.Detaching => this with
        {
            Activity = GitSourceActivity.Paused,
            Revision = checked(Revision + 1)
        },
        GitSourceActivity.Paused => this,
        _ => throw new InvalidOperationException("Only active sources can be paused.")
    };

    public RepositorySource Resume()
    {
        if (Activity != GitSourceActivity.Paused)
            throw new InvalidOperationException("Only paused sources can be resumed.");

        return this with { Activity = GitSourceActivity.Active, Revision = checked(Revision + 1) };
    }

    public RepositorySource BeginDetach()
    {
        if (Activity is not (GitSourceActivity.Active or GitSourceActivity.Paused))
            throw new InvalidOperationException("Only active or paused sources can begin detaching.");

        return this with { Activity = GitSourceActivity.Detaching, Revision = checked(Revision + 1) };
    }

    public RepositorySource CompleteDetach(long ownedResourceCount)
    {
        if (Activity != GitSourceActivity.Detaching || EvaluateDeletion(ownedResourceCount) !=
            SourceDeletionEligibility.Allowed)
            throw new InvalidOperationException("Detach completes only after all ownership is released.");

        return this with { Activity = GitSourceActivity.Detached, Revision = checked(Revision + 1) };
    }

    public RepositorySource Delete(long ownedResourceCount)
    {
        if (Activity == GitSourceActivity.Detaching || EvaluateDeletion(ownedResourceCount) !=
            SourceDeletionEligibility.Allowed)
            throw new InvalidOperationException("Source deletion requires completed ownership release.");

        return Activity == GitSourceActivity.Deleted
            ? this
            : this with { Activity = GitSourceActivity.Deleted, Revision = checked(Revision + 1) };
    }

    public SourceDeletionEligibility EvaluateDeletion(long ownedResourceCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ownedResourceCount);
        return ownedResourceCount == 0 ? SourceDeletionEligibility.Allowed : SourceDeletionEligibility.DetachRequired;
    }
}

public enum SourceDeletionEligibility { Allowed, DetachRequired }

public static class RepositorySourceFactory
{
    public static RepositorySource Create(
        GitSourceIdentity identity,
        GitRepositoryLocation repository,
        GitSyncPolicy sync,
        GitSourceAudit audit,
        string creationKey,
        string creationHash,
        string displayUrl,
        GitPrunePolicy prune = GitPrunePolicy.Retain)
    {
        if (!Enum.IsDefined(prune))
            throw new ArgumentOutOfRangeException(nameof(prune));

        GitSourceGuards.Identity(identity);
        GitSourceGuards.Required(repository.Provider, nameof(repository.Provider));
        GitSourceGuards.Required(creationKey, nameof(creationKey));
        GitSourceGuards.Required(creationHash, nameof(creationHash));
        GitSourceGuards.Required(displayUrl, nameof(displayUrl));
        GitSourceGuards.Required(repository.ConnectionId, nameof(repository.ConnectionId));
        GitSourceGuards.Required(repository.ExternalRepositoryId, nameof(repository.ExternalRepositoryId));
        GitSourceGuards.Required(repository.Branch, nameof(repository.Branch));
        GitSourceGuards.Required(audit.CreatedByUserId, nameof(audit.CreatedByUserId));
        GitSourceGuards.Utc(audit.CreatedAtUtc);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sync.PollInterval, TimeSpan.Zero);

        if (!RepositoryManifestDirectory.IsValid(repository.ManifestDirectory))
            throw new ArgumentException(
                "A safe repository-relative manifest directory is required.",
                nameof(repository));

        return new(identity.SourceId,
            identity.TenantId,
            repository,
            1,
            sync,
            GitSourceActivity.Active,
            prune,
            audit,
            creationKey,
            creationHash,
            displayUrl);
    }
}

internal static class GitSourceGuards
{
    internal static void Identity(GitSourceIdentity identity)
    {
        Required(identity.TenantId, nameof(identity.TenantId));
        Required(identity.SourceId, nameof(identity.SourceId));
    }

    internal static void Required(string value, string name) => ArgumentException.ThrowIfNullOrWhiteSpace(value, name);

    internal static void Utc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("A UTC timestamp is required.", nameof(value));
    }
}
