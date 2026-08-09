using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.WebServices.Domain;

public enum WebServiceReleaseAction
{
    Imported,
    Create,
    Update,
    Redeploy,
    Rollback,
    ImageTagUpdate,
    SourceBuild
}

public enum WebServiceReleaseOutcome
{
    Pending,
    Succeeded,
    Failed,
    Superseded
}

public sealed record WebServiceRelease(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string WebServiceId,
    int Revision,
    [property: BsonRepresentation(BsonType.String)]
    WebServiceReleaseAction Action,
    [property: BsonRepresentation(BsonType.String)]
    WebServiceReleaseOutcome Outcome,
    string? ActorUserId,
    string? ActorEmail,
    DateTime RequestedAtUtc,
    DateTime? CompletedAtUtc,
    string? FailureMessage,
    string? TraceId,
    string? CorrelationId,
    WebServiceReleaseProvenance? Provenance,
    long DeploymentGeneration,
    WebServiceSpec Spec
);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(ExternalImageTagUpdateProvenance), typeof(SourceBuildReleaseProvenance))]
public abstract record WebServiceReleaseProvenance;

public sealed record ExternalImageTagUpdateProvenance(
    string Source,
    string? CommitSha,
    string? BuildUrl,
    string? ImageDigest,
    string PreviousImageTag,
    string NewImageTag,
    string ActorType,
    string? DeploymentTokenId,
    string? DeploymentTokenName) : WebServiceReleaseProvenance;

public sealed record SourceBuildReleaseProvenance(
    string BuildJobId,
    string SourceRepositoryId,
    string CommitSha,
    string ImageDigest,
    string PreviousImageReference,
    string NewImageReference) : WebServiceReleaseProvenance;

public static class WebServiceReleaseFactory
{
    public static WebServiceRelease CreatePending(
        string tenantId,
        string webServiceId,
        int revision,
        WebServiceReleaseAction action,
        string? actorUserId,
        string? actorEmail,
        WebServiceSpec spec,
        string? traceId,
        string? correlationId,
        DateTime? requestedAtUtc = null,
        WebServiceReleaseProvenance? provenance = null,
        long deploymentGeneration = 1) =>
        new(
            Id: ObjectId.GenerateNewId().ToString(),
            TenantId: tenantId,
            WebServiceId: webServiceId,
            Revision: revision,
            Action: action,
            Outcome: WebServiceReleaseOutcome.Pending,
            ActorUserId: actorUserId,
            ActorEmail: actorEmail,
            RequestedAtUtc: requestedAtUtc ?? DateTime.UtcNow,
            CompletedAtUtc: null,
            FailureMessage: null,
            TraceId: traceId,
            CorrelationId: correlationId,
            Provenance: provenance,
            DeploymentGeneration: deploymentGeneration,
            Spec: spec);
}
