namespace Pole.Features.SeaweedFS.Application.Kubernetes;

internal static class SeaweedFSNames
{
    public static string Instance(string instanceId) => $"sw-{instanceId}";

    public static string S3Service(string instanceId) => $"{Instance(instanceId)}-s3";

    public static string Bucket(string bucketId) => $"b-{bucketId}";

    public static string BucketPolicy(string bucketId) => $"p-{bucketId}";
}
