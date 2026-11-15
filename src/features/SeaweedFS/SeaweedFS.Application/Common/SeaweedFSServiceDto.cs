using System.Collections.Immutable;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SeaweedFS.Domain;

namespace Pole.Features.SeaweedFS.Application.Common;

public sealed record SeaweedFSStorageDto(
    int SizeGb,
    string StorageProfileId
);

public sealed record SeaweedFSSpecDto(
    SeaweedFSMode Mode,
    SeaweedFSStorageDto Storage,
    string? ExistingPostgresId = null
)
{
    public SeaweedFSSpec ToDomain() => new(
        Mode,
        new(Storage.SizeGb, Storage.StorageProfileId),
        ExistingPostgresId is not null ? new ExistingPostgresSeaweedFSMetadata(ExistingPostgresId) :
        Mode == SeaweedFSMode.SingleNode ? new EmbeddedSeaweedFSMetadata() : new DedicatedPostgresSeaweedFSMetadata());
}

public sealed record SeaweedFSServiceDto(
    string Id,
    string Name,
    string ResourceGroupId,
    SeaweedFSSpecDto Spec,
    ImmutableHashSet<string> Tags,
    PlacementSummaryDto? Placement,
    long Revision,
    string Lifecycle,
    DateTime? DeletionDeadlineUtc
);

public sealed record SeaweedFSBucketDto(
    string Id,
    string SeaweedFSId,
    string Name,
    string SecretId,
    long Revision,
    string Lifecycle,
    DateTime? DeletionDeadlineUtc
);

public static class SeaweedFSDtoExtensions
{
    public static string State(this SeaweedFSLifecycle lifecycle) => lifecycle switch
    {
        ActiveSeaweedFSLifecycle => "Active", DeletingSeaweedFSLifecycle => "Deleting",
        PurgingSeaweedFSLifecycle => "Purging", DeletedSeaweedFSLifecycle => "Deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle))
    };

    public static DateTime? Deadline(this SeaweedFSLifecycle lifecycle) => lifecycle switch
    {
        DeletingSeaweedFSLifecycle deleting => deleting.DeletionDeadlineUtc,
        PurgingSeaweedFSLifecycle purging => purging.DeletionDeadlineUtc,
        _ => null
    };

    public static SeaweedFSServiceDto ToDto(this SeaweedFSService resource, PlacementSummaryDto? placement = null) =>
        new(
            resource.Id,
            resource.Name,
            resource.ResourceGroupId,
            new(
                resource.Spec.Mode,
                new(resource.Spec.Storage.SizeGb, resource.Spec.Storage.StorageProfileId),
                (resource.Spec.Metadata as ExistingPostgresSeaweedFSMetadata)?.PostgresId),
            resource.Tags,
            placement,
            resource.Revision,
            resource.Lifecycle.State(),
            resource.Lifecycle.Deadline());

    public static SeaweedFSBucketDto ToDto(this SeaweedFSBucket bucket) => new(
        bucket.Id,
        bucket.SeaweedFSId,
        bucket.Name,
        bucket.SecretId,
        bucket.Revision,
        bucket.Lifecycle.State(),
        bucket.Lifecycle.Deadline());
}
