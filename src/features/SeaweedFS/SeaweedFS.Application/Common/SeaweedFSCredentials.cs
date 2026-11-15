using System.Security.Cryptography;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.SeaweedFS.Application.Common;

public static class SeaweedFSCredentials
{
    public const string AdminPurpose = "admin";
    public const string AccessKey = "accessKey";
    public const string SecretKey = "secretKey";
    public static string BucketPurpose(string bucketId) => "bucket:" + bucketId;
    public static ManagedSecretOwner AdminOwner(string serviceId) => new("pole/seaweedfs", serviceId, AdminPurpose);

    public static ManagedSecretOwner BucketOwner(string serviceId, string bucketId) => new(
        "pole/seaweedfs",
        serviceId,
        BucketPurpose(bucketId));

    public static PrepareManagedSecretRequest Prepare(
        string tenantId,
        string secretId,
        ManagedSecretOwner owner,
        long version,
        ManagedSecretSharingPolicy sharing) => new(
        tenantId,
        secretId,
        owner,
        $"seaweedfs-{secretId}",
        version,
        new Dictionary<string, string>
        {
            [AccessKey] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            [SecretKey] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        },
        ManagedSecretKubernetesType.Opaque,
        sharing);
}

public sealed record SeaweedFSCredentialsDto(
    string AccessKey,
    string SecretKey
)
{
    public override string ToString() => "SeaweedFSCredentialsDto([REDACTED])";
}
