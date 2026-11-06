using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.SourceBuilds.Domain;

public sealed record SourceBuildPromotionReceipt(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string ConfigurationId,
    string WebServiceId,
    long ConfigurationRevision,
    long Generation,
    string SourceRepositoryId,
    string CommitSha,
    SourceBuildArtifact Artifact,
    string ReleaseId,
    long WebServiceRevision) : ITenantOwned;

public static class SourceBuildPromotionReceiptFactory
{
    public static SourceBuildPromotionReceipt Create(
        SourceBuildJob job,
        SourceBuildArtifact artifact,
        string releaseId,
        long webServiceRevision)
    {
        if (string.IsNullOrWhiteSpace(releaseId) || webServiceRevision <= 0)
            throw new ArgumentException("Promotion requires an artifact and a committed workload revision.");

        ArgumentNullException.ThrowIfNull(artifact);
        var promotedArtifact = SourceBuildArtifactFactory.Create(artifact.Reference, artifact.Digest);
        if (job.State is not PushingSourceBuild pushing || pushing.Artifact != promotedArtifact)
            throw new ArgumentException("A promotion receipt requires the built artifact.", nameof(job));

        return new(job.Id, job.TenantId, job.ConfigurationId, job.WebServiceId,
            job.InputVersion.ConfigurationRevision, job.InputVersion.Generation, job.SourceRepositoryId, job.CommitSha,
            promotedArtifact, releaseId,
            webServiceRevision);
    }
}
