using System.Collections.Immutable;
using MongoDB.Bson;
using Pole.Features.SeaweedFS.Domain;
using Xunit;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Endpoints;

namespace Pole.Features.SeaweedFS.Tests;

public sealed class SeaweedFSServiceTests
{
    [Theory]
    [InlineData("app.assets", true)]
    [InlineData("123", true)]
    [InlineData("filemeta", false)]
    [InlineData("app..assets", false)]
    [InlineData("999.0.0.1", false)]
    [InlineData("xn--name", false)]
    [InlineData("test-s3alias", false)]
    public void Managed_bucket_names_match_the_operator_and_filer(string name, bool accepted)
    {
        var id = ObjectId.GenerateNewId().ToString();
        var result = new CreateSeaweedFSBucketValidator().Validate(new CreateSeaweedFSBucketRequest(id, new(name), id));
        Assert.Equal(accepted, result.IsValid);
    }

    [Fact]
    public void Deletion_persists_the_original_deadline_and_is_idempotent()
    {
        var service = SeaweedFSFactory.Create(
            ObjectId.GenerateNewId().ToString(),
            "test",
            ObjectId.GenerateNewId().ToString(),
            new(
                SeaweedFSMode.SingleNode,
                new(10, ObjectId.GenerateNewId().ToString()),
                new EmbeddedSeaweedFSMetadata()),
            ImmutableHashSet<string>.Empty,
            ObjectId.GenerateNewId().ToString(),
            new(100, 128, 4, 12));
        var now = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        var deleting = service.BeginDeletion(now, TimeSpan.FromHours(6));
        Assert.Equal(
            now.AddHours(6),
            Assert.IsType<DeletingSeaweedFSLifecycle>(deleting.Lifecycle).DeletionDeadlineUtc);
        Assert.Same(deleting, deleting.BeginDeletion(now.AddHours(1), TimeSpan.Zero));
        Assert.Equal(2, deleting.Revision);
    }

    [Theory]
    [InlineData(SeaweedFSMode.SingleNode, false, 4, 12)]
    [InlineData(SeaweedFSMode.Replicated, false, 15, 36)]
    [InlineData(SeaweedFSMode.Replicated, true, 12, 33)]
    public void Quota_counts_every_physical_pod_and_pvc(
        SeaweedFSMode mode,
        bool existing,
        long pods,
        long diskGb)
    {
        var totals = SeaweedFSTopology.Calculate(mode, 250, 512, 10, existing);
        Assert.Equal(pods, totals.WorkloadInstances);
        Assert.Equal(pods * 250, totals.CpuMillis);
        Assert.Equal(pods * 512, totals.MemoryMi);
        Assert.Equal(diskGb, totals.StorageGb);
    }

    [Fact]
    public void Create_validation_rejects_missing_storage_and_single_node_external_metadata()
    {
        var validator = new CreateSeaweedFSServiceRequestBodyValidator();
        var group = ObjectId.GenerateNewId().ToString();
        var missing = new CreateSeaweedFSServiceRequestBody(
            "test",
            group,
            new(SeaweedFSMode.SingleNode, null!),
            ImmutableHashSet<string>.Empty);
        Assert.False(validator.Validate(missing).IsValid);
        var incompatible = missing with
        {
            Spec = new(
                SeaweedFSMode.SingleNode,
                new(10, ObjectId.GenerateNewId().ToString()),
                ObjectId.GenerateNewId().ToString())
        };
        Assert.False(validator.Validate(incompatible).IsValid);
    }
}
