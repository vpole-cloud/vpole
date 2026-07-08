using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Domain;

public enum WebServiceReleaseAction
{
    Imported,
    Create,
    Update,
    Redeploy,
    Rollback
}

public enum WebServiceReleaseOutcome
{
    Pending,
    Succeeded,
    Failed
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
    WebServiceSpecSnapshot Spec
);

public static class WebServiceReleaseFactory
{
    public static WebServiceRelease CreatePending(
        string tenantId,
        string webServiceId,
        int revision,
        WebServiceReleaseAction action,
        string? actorUserId,
        string? actorEmail,
        WebServiceSpecSnapshot spec,
        string? traceId,
        string? correlationId,
        DateTime? requestedAtUtc = null) =>
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
            Spec: spec);
}
