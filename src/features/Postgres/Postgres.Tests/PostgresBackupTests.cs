// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.FSharp.Core;
using Pole.Features.Backups.Contracts;
using Pole.Features.Postgres.Application.Backups;
using Pole.Scheduling;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresBackupTests
{
    private static readonly BackupArchiveContext Archive = new(
        new("archive", "origin", new("https://objects.invalid", "bucket", "nested/prefix")),
        new("tenant", "pole/postgresql", "service"),
        new("17", 10, 1, 1),
        new("group", "scope", "target", "cell", "namespace", "deployment", "storage", 1, "compute", 1));

    private static readonly BackupCredentialProjection Projection = new("projected-secret", "accessKey", "secretKey");

    [Fact]
    public void PausedScheduleKeepsRetentionAndWalArchiving()
    {
        var store = PostgresBackupManifests.store(Archive, Projection, FSharpOption<int>.Some(14));
        var config = store["spec"]!["configuration"]!;
        Assert.Equal("s3://bucket/nested/prefix", config["destinationPath"]!.GetValue<string>());
        Assert.Equal("14d", store["spec"]!["retentionPolicy"]!.GetValue<string>());
        Assert.Null(config["serverName"]);
        Assert.Equal("projected-secret", config["s3Credentials"]!["accessKeyId"]!["name"]!.GetValue<string>());
        var schedule = PostgresBackupManifests.schedule(Archive, new("5 2 * * *", true, 14), "source-uid");
        Assert.Equal("0 5 2 * * *", schedule["spec"]!["schedule"]!.GetValue<string>());
        Assert.True(schedule["spec"]!["suspend"]!.GetValue<bool>());
        Assert.Equal("source-uid", schedule["metadata"]!["ownerReferences"]![0]!["uid"]!.GetValue<string>());
        var plugin = PostgresBackupManifests.walPlugin(Archive);
        Assert.True(plugin["isWALArchiver"]!.GetValue<bool>());
        Assert.Equal("origin", plugin["parameters"]!["serverName"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("0 1 * * 7", "0 0 1 * * 0", "2026-11-15T01:00:00Z")]
    [InlineData("0 1 * * 5-7", "0 0 1 * * 0,5,6", "2026-11-13T01:00:00Z")]
    [InlineData("0 1 1 * */2", "0 0 1 1 * 0,2,4,6", "2026-11-12T01:00:00Z")]
    [InlineData("0 1 */2 * 1", "0 0 1 1,3,5,7,9,11,13,15,17,19,21,23,25,27,29,31 * 1", "2026-11-13T01:00:00Z")]
    [InlineData("0 1 1 * 0-7", "0 0 1 1 * *", "2026-12-01T01:00:00Z")]
    [InlineData("0 1 1 * 0-6", "0 0 1 1 * *", "2026-12-01T01:00:00Z")]
    [InlineData("0 1 */1 * 1", "0 0 1 * * 1", "2026-11-16T01:00:00Z")]
    [InlineData("0 1 1-31 * 1", "0 0 1 * * 1", "2026-11-16T01:00:00Z")]
    public void SchedulePreservesPoleDaySemanticsForCnpg(
        string expression,
        string expectedNative,
        string expectedNext)
    {
        var schedule = PostgresBackupManifests.schedule(Archive, new(expression, false, 14), "source-uid");
        var native = schedule["spec"]!["schedule"]!.GetValue<string>();
        Assert.Equal(expectedNative, native);
        var after = DateTimeOffset.Parse("2026-11-12T00:00:00Z");
        var next = DateTimeOffset.Parse(expectedNext);
        Assert.Equal(next, UtcCronSchedule.Parse(expression).GetNextOccurrence(after));
        Assert.Equal(next, UtcCronSchedule.Parse(native[2..]).GetNextOccurrence(after));
    }

    [Theory]
    [InlineData("status=DONE")]
    [InlineData("status=invalid")]
    [InlineData("status=DONE\nend_time=not-a-time")]
    [InlineData("status=STARTED\nstatus=DONE")]
    [InlineData("status=DONE\nend_time=2026-11-12T16:30:00")]
    public void UnreadableCatalogEntryIsAnError(string metadata) =>
        Assert.Throws<InvalidDataException>(() => BarmanCatalog.parseEntry("20261112T091100", metadata));

    [Fact]
    public async Task LiveCatalogReadsOnlySelectedArchiveMetadataAcrossAllPages()
    {
        using var client = new MetadataClient();
        var items = await BarmanCatalog.readWith(client, Archive.Location, CancellationToken.None);
        var entry = Assert.Single(items);
        Assert.Equal("20261112T091100", entry.BackupId);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 16, 30, 0, TimeSpan.Zero), entry.CompletedAt);
        Assert.Equal(2, client.Pages);
        Assert.Equal(2, client.MetadataReads);
    }

    private sealed class MetadataClient() : AmazonS3Client(
        new AnonymousAWSCredentials(),
        new AmazonS3Config { ServiceURL = Archive.Location.Storage.Endpoint })
    {
        public int Pages { get; private set; }
        public int MetadataReads { get; private set; }

        public override Task<ListObjectsV2Response> ListObjectsV2Async(
            ListObjectsV2Request request,
            CancellationToken ct)
        {
            Assert.Equal(Archive.Location.Storage.Bucket, request.BucketName);
            Assert.Equal("nested/prefix/origin/base/", request.Prefix);
            Assert.Equal("/", request.Delimiter);
            Assert.Equal(Pages == 0 ? null : "next", request.ContinuationToken);
            var first = Pages++ == 0;
            return Task.FromResult(
                new ListObjectsV2Response
                {
                    CommonPrefixes = [request.Prefix + (first ? "20261112T091100/" : "20261112T091200/")],
                    IsTruncated = first,
                    NextContinuationToken = first ? "next" : null,
                });
        }

        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken ct)
        {
            Assert.Equal(Archive.Location.Storage.Bucket, request.BucketName);
            Assert.EndsWith("/backup.info", request.Key);
            MetadataReads++;
            var text = request.Key.Contains("091100")
                ? "irrelevant=value\nstatus=DONE\nend_time=2026-11-12 18:30:00+02:00\n"
                : "status=STARTED\nend_time=None\n";
            return Task.FromResult(
                new GetObjectResponse { ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        }
    }
}
