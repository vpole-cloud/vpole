using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.ComputeProfiles.Domain;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

public sealed class ComputeProfileRevisionTests
{
    [Fact]
    public void Factory_derives_revision_identity_and_preserves_the_exact_snapshot()
    {
        var snapshot = ComputeProfileTestData.CreateProfile("507f1f77bcf86cd799439096");
        var revision = ComputeProfileRevisionFactory.Create(snapshot);

        Assert.Equal($"{snapshot.Id}/{snapshot.Revision}", revision.Id);
        Assert.Equal(snapshot.Id, revision.ComputeProfileId);
        Assert.Equal(snapshot.Revision, revision.Revision);
        Assert.Same(snapshot, revision.Snapshot);

        var restored = BsonSerializer.Deserialize<ComputeProfileRevision>(revision.ToBson());
        Assert.Equal(revision.Id, restored.Id);
        Assert.Equal(restored.ComputeProfileId, restored.Snapshot.Id);
        Assert.Equal(restored.Revision, restored.Snapshot.Revision);
        Assert.Equal(snapshot.Resources, restored.Snapshot.Resources);
    }

    [Fact]
    public void Factory_rejects_a_missing_snapshot_or_invalid_revision_identity()
    {
        var snapshot = ComputeProfileTestData.CreateProfile("507f1f77bcf86cd799439096");

        Assert.Throws<ArgumentNullException>(() => ComputeProfileRevisionFactory.Create(null!));
        Assert.Throws<ArgumentException>(() => ComputeProfileRevisionFactory.Create(snapshot with { Id = "invalid" }));
        Assert.Throws<ArgumentException>(() => ComputeProfileRevisionFactory.Create(snapshot with { Revision = 0 }));
    }
}
