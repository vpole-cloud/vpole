namespace Pole.Features.SeaweedFS.Application.Lifecycle;

public sealed record PurgeSeaweedFSBucketDataRequest(
    string TenantId,
    string SeaweedFSId,
    string BucketId,
    string BucketName
);

public sealed record SeaweedFSBucketDataPurged;

public sealed record SeaweedFSBucketPurgeRetryableFailure(
    string Code
);

public union PurgeSeaweedFSBucketDataOutcome(SeaweedFSBucketDataPurged, SeaweedFSBucketPurgeRetryableFailure);

public delegate Task<PurgeSeaweedFSBucketDataOutcome> PurgeSeaweedFSBucketData(
    PurgeSeaweedFSBucketDataRequest request,
    CancellationToken cancellationToken = default);
