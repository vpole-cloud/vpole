using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.Core.Multitenancy;

namespace Pole.Features.WebServices.Domain;

public enum WebServiceReleaseAction
{
    Create, Update, Redeploy, Rollback, ImageTagUpdate, SourceBuild, AddDeployment, UpdateDeployment, RemoveDeployment,
    RetryDeployment, HostnameChanged
}

public enum WebServiceReleaseOutcome { Pending, Succeeded, PartiallyFailed, Failed, Superseded }

public enum WebServiceReleaseDeploymentOperation { Apply, Delete }

public enum WebServiceReleaseDeploymentOutcome { Pending, Succeeded, Failed, Removed }

public sealed record WebServiceReleaseDeployment(
    string ResourceDeploymentId,
    string DeploymentTargetId,
    long ResourceRevision,
    long DeploymentGeneration,
    [property: BsonRepresentation(BsonType.String)]
    WebServiceReleaseDeploymentOperation Operation,
    [property: BsonRepresentation(BsonType.String)]
    WebServiceReleaseDeploymentOutcome Outcome,
    DateTime? CompletedAtUtc,
    string? FailureCode,
    string? FailureMessage)
{
    public bool IsTerminal => Outcome is not WebServiceReleaseDeploymentOutcome.Pending;
}

public sealed record WebServiceRelease(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string WebServiceId,
    long ResourceRevision,
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
    ImmutableList<WebServiceReleaseDeployment> Deployments,
    WebServiceSpec Spec,
    WebServiceExposure Exposure) : ITenantOwned;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(ExternalImageTagUpdateProvenance),
    typeof(SourceBuildReleaseProvenance),
    typeof(GitOpsReleaseProvenance))]
public abstract record WebServiceReleaseProvenance;

public sealed record ExternalImageTagUpdateProvenance(
    string Source,
    string? CommitSha,
    string? BuildUrl,
    string? ImageDigest,
    string? PreviousImageTag,
    string NewImageTag,
    string ActorType,
    string? DeploymentTokenId,
    string? DeploymentTokenName) : WebServiceReleaseProvenance;

public sealed record SourceBuildReleaseProvenance(
    string BuildJobId,
    string SourceRepositoryId,
    string CommitSha,
    string ImageDigest,
    string? PreviousImageReference,
    string NewImageReference) : WebServiceReleaseProvenance;

public sealed record GitOpsReleaseProvenance(
    string SourceId,
    string RunId,
    string CommitSha,
    string? PreviousImageTag,
    string NewImageTag) : WebServiceReleaseProvenance;

public static class WebServiceReleaseFactory
{
    public sealed record CreateOptions(
        string Id,
        DateTime RequestedAtUtc);

    public static WebServiceRelease CreatePending(
        string tenantId,
        string webServiceId,
        long resourceRevision,
        WebServiceReleaseAction action,
        string? actorUserId,
        string? actorEmail,
        WebServiceSpec spec,
        IEnumerable<WebServiceReleaseDeployment> deployments,
        string? traceId,
        string? correlationId,
        CreateOptions options,
        WebServiceExposure exposure,
        WebServiceReleaseProvenance? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(exposure);
        ArgumentNullException.ThrowIfNull(deployments);
        var entries = deployments.ToImmutableList();
        if (entries.Count == 0)
            throw new ArgumentException("A release must snapshot at least one deployment.", nameof(deployments));
        if (entries.Select(entry => entry.ResourceDeploymentId).Distinct(StringComparer.Ordinal).Count() !=
            entries.Count)
            throw new ArgumentException("A release may contain each deployment once.", nameof(deployments));
        var requestedAtUtc = WebServiceFactory.Utc(options.RequestedAtUtc);
        var outcome = Aggregate(entries);
        return new WebServiceRelease(options.Id,
            tenantId,
            webServiceId,
            resourceRevision,
            action,
            outcome,
            actorUserId,
            actorEmail,
            requestedAtUtc,
            outcome is WebServiceReleaseOutcome.Pending ? null : requestedAtUtc,
            null,
            traceId,
            correlationId,
            provenance,
            entries,
            spec,
            exposure);
    }

    public static WebServiceRelease ApplyCompletion(
        WebServiceRelease release,
        string resourceDeploymentId,
        long resourceRevision,
        long deploymentGeneration,
        WebServiceReleaseDeploymentOperation operation,
        bool succeeded,
        bool retryable,
        string? failureCode,
        string? failureMessage,
        DateTime completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.Outcome is WebServiceReleaseOutcome.Superseded || retryable)
            return release;
        var entry = release.Deployments.SingleOrDefault(item =>
            item.ResourceDeploymentId == resourceDeploymentId && item.ResourceRevision == resourceRevision &&
            item.DeploymentGeneration == deploymentGeneration && item.Operation == operation);
        if (entry is null || entry.Outcome is not WebServiceReleaseDeploymentOutcome.Pending)
            return release;
        var completed = entry with
        {
            Outcome = succeeded
                ? operation == WebServiceReleaseDeploymentOperation.Delete
                    ? WebServiceReleaseDeploymentOutcome.Removed
                    : WebServiceReleaseDeploymentOutcome.Succeeded
                : WebServiceReleaseDeploymentOutcome.Failed,
            CompletedAtUtc = WebServiceFactory.Utc(completedAtUtc),
            FailureCode = succeeded ? null : failureCode,
            FailureMessage = succeeded ? null : failureMessage
        };
        var deployments = release.Deployments.Replace(entry, completed);
        var outcome = Aggregate(deployments);
        var aggregateFailureMessage = deployments
            .Where(deployment => deployment.Outcome == WebServiceReleaseDeploymentOutcome.Failed)
            .OrderBy(deployment => deployment.ResourceDeploymentId, StringComparer.Ordinal)
            .Select(deployment => deployment.FailureMessage)
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
        return release with
        {
            Deployments = deployments,
            Outcome = outcome,
            CompletedAtUtc = outcome is WebServiceReleaseOutcome.Pending ? null : completed.CompletedAtUtc,
            FailureMessage = outcome is WebServiceReleaseOutcome.Failed or WebServiceReleaseOutcome.PartiallyFailed
                ? aggregateFailureMessage
                : null
        };
    }

    public static WebServiceRelease Supersede(WebServiceRelease release, DateTime atUtc) =>
        release.Outcome is WebServiceReleaseOutcome.Pending
            ? release with
            {
                Outcome = WebServiceReleaseOutcome.Superseded,
                CompletedAtUtc = WebServiceFactory.Utc(atUtc),
                FailureMessage = null
            }
            : release;

    public static WebServiceReleaseOutcome Aggregate(IEnumerable<WebServiceReleaseDeployment> entries)
    {
        var values = entries.ToArray();
        if (values.Any(entry => entry.Outcome == WebServiceReleaseDeploymentOutcome.Pending))
            return WebServiceReleaseOutcome.Pending;
        var failures = values.Count(entry => entry.Outcome == WebServiceReleaseDeploymentOutcome.Failed);
        return failures == 0 ? WebServiceReleaseOutcome.Succeeded
            : failures == values.Length ? WebServiceReleaseOutcome.Failed
            : WebServiceReleaseOutcome.PartiallyFailed;
    }
}
