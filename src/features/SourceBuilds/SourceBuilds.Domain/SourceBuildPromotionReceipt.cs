using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

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
    string ImageReference,
    string ImageDigest,
    string ReleaseId,
    long WebServiceRevision);

public static class SourceBuildPromotionReceiptFactory
{
    public static SourceBuildPromotionReceipt Create(
        SourceBuildJob job,
        string imageDigest,
        string releaseId,
        long webServiceRevision)
    {
        if (string.IsNullOrWhiteSpace(imageDigest) || string.IsNullOrWhiteSpace(releaseId) || webServiceRevision <= 0)
            throw new ArgumentException("Promotion requires an artifact and a committed workload revision.");

        if (job.State is not PushingSourceBuild pushing || pushing.ExpectedDigest != imageDigest)
            throw new ArgumentException("A promotion receipt requires the built artifact.", nameof(job));

        return new(job.Id, job.TenantId, job.ConfigurationId, job.WebServiceId,
            job.InputVersion.ConfigurationRevision, job.InputVersion.Generation, job.SourceRepositoryId, job.CommitSha,
            pushing.ImageReference, imageDigest, releaseId,
            webServiceRevision);
    }
}
