using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitOps.Domain;

public sealed record GitSourceIdentity(
    string TenantId,
    string SourceId);

public sealed record GitRepositoryLocation(
    string ConnectionId,
    string ExternalRepositoryId,
    string Branch,
    string ManifestDirectory);

public enum GitSourceActivity { Active, Paused }

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
    GitSourceAudit Audit)
{
    public RepositorySource Pause() => Activity == GitSourceActivity.Paused ? this : this with
    {
        Activity = GitSourceActivity.Paused,
        Revision = checked(Revision + 1)
    };

    public RepositorySource Resume() => Activity == GitSourceActivity.Active ? this : this with
    {
        Activity = GitSourceActivity.Active,
        Revision = checked(Revision + 1)
    };

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
        GitSourceAudit audit)
    {
        GitSourceGuards.Identity(identity);
        GitSourceGuards.Required(repository.ConnectionId, nameof(repository.ConnectionId));
        GitSourceGuards.Required(repository.ExternalRepositoryId, nameof(repository.ExternalRepositoryId));
        GitSourceGuards.Required(repository.Branch, nameof(repository.Branch));
        GitSourceGuards.Required(audit.CreatedByUserId, nameof(audit.CreatedByUserId));
        GitSourceGuards.Utc(audit.CreatedAtUtc);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sync.PollInterval, TimeSpan.Zero);

        var directory = repository.ManifestDirectory;
        if (directory is null || directory.Contains('\\') || directory.StartsWith('/') ||
            directory.Split('/').Any(segment => segment is "." or "..") ||
            directory.Any(char.IsControl))
            throw new ArgumentException("A repository-relative manifest directory is required.", nameof(repository));

        return new(identity.SourceId, identity.TenantId, repository with
        {
            ManifestDirectory = directory.TrimEnd('/')
        }, 1, sync, GitSourceActivity.Active, GitPrunePolicy.Retain, audit);
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
