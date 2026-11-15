using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using Pole.Testing;
using Xunit;

namespace Pole.Features.PrivateAccess.Tests.Integration;

public sealed class SeaweedFSBrokerTransportTests
{
    private const string SeaweedImage = "docker.io/chrislusf/seaweedfs:4.53";

    [Fact]
    public async Task SigV4_authenticated_S3_put_and_get_cross_the_production_Private_Access_broker()
    {
        ContainerRuntimeBootstrap.ConfigureFromLabStateIfAvailable();
        var identity = TestRunIdentity.Create();
        var accessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var secretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var configuration = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                identities = new[]
                {
                    new
                    {
                        name = "broker-test-owner",
                        credentials = new[] { new { accessKey, secretKey } },
                        actions = new[] { "Admin", "Read", "Write", "List", "Tagging" }
                    }
                }
            });
        await using var seaweed = new ContainerBuilder(SeaweedImage).WithTestOwnership(identity)
            .WithPortBinding(8333, true)
            .WithResourceMapping(configuration, "/config/s3.json")
            .WithCommand(
                "server",
                "-dir=/data",
                "-ip=127.0.0.1",
                "-ip.bind=0.0.0.0",
                "-s3",
                "-s3.config=/config/s3.json")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilInternalTcpPortIsAvailable(8333, wait => wait.WithTimeout(TimeSpan.FromSeconds(30))))
            .Build();
        await seaweed.StartAsync();
        await using var broker = await DatabaseBrokerFixture.StartAsync(
            seaweed.GetMappedPublicPort(8333),
            "pole/seaweedfs",
            "s3");
        await using var forward = await broker.ForwardAsync();
        var config = new AmazonS3Config
        {
            ServiceURL = $"http://127.0.0.1:{forward.Port}",
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        using var s3 = new AmazonS3Client(accessKey, secretKey, config);
        const string bucket = "broker-roundtrip";
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        var payload = RandomNumberGenerator.GetBytes(128 * 1024);
        using var input = new MemoryStream(payload, writable: false);
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = bucket,
                Key = "binary-object",
                InputStream = input,
                UseChunkEncoding = false
            });
        using var response = await s3.GetObjectAsync(bucket, "binary-object");
        using var copied = new MemoryStream();
        await response.ResponseStream.CopyToAsync(copied);
        Assert.Equal(payload, copied.ToArray());
        using var wrongSignature = new AmazonS3Client(
            accessKey,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            config);
        var rejected
            = await Assert.ThrowsAsync<AmazonS3Exception>(() => wrongSignature.GetObjectAsync(bucket, "binary-object"));
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.True(broker.StartedSessions > 0);
    }
}
