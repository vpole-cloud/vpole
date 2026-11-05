using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Domains.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Deployments;

public sealed record WebServiceDeploymentCompletion(
    string ResourceDeploymentId,
    long ResourceRevision,
    long DeploymentGeneration,
    WebServiceReleaseDeploymentOperation Operation,
    bool Succeeded,
    bool Retryable,
    DateTime CompletedAtUtc,
    string? FailureCode,
    string? FailureMessage,
    EventCore EventCore);

public sealed record WebServiceDeploymentCompletionRecorded;

public union CompleteWebServiceDeploymentOutcome(
    WebServiceDeploymentCompletionRecorded,
    WebServiceFinalizationError);

public delegate Task<CompleteWebServiceDeploymentOutcome> CompleteWebServiceDeployment(
    string tenantId,
    string webServiceId,
    WebServiceDeploymentCompletion completion,
    CancellationToken cancellationToken = default);

internal sealed class MongoCompleteWebServiceDeployment(
    IMongoCollection<WebServiceRelease> releases,
    IMongoCollection<WebService> webServices,
    RunMongoOutcomeTransaction<CompleteWebServiceDeploymentOutcome> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites,
    LoadHostnameBindingsForDeploymentInTransaction loadHostnameBindingsForDeployment,
    DeleteHostnameBindingsForDeployment deleteHostnameBindingsForDeployment,
    GetExactResourceDeploymentFinalizationReadiness getExactResourceDeploymentFinalizationReadiness,
    PurgeExactDeletedResourceDeployment purgeExactDeletedResourceDeployment)
{
    public Task<CompleteWebServiceDeploymentOutcome> InvokeAsync(
        string tenantId,
        string webServiceId,
        WebServiceDeploymentCompletion completion,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                completion.EventCore.TenantId,
                tenantId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A web service deployment completion must preserve tenant identity.");
        }

        return runMongoTransaction(async (transaction, ct) =>
        {
            var release = await releases.Find(
                transaction.Session,
                WebServiceReleaseFilters.PendingDeployment(
                    tenantId,
                    webServiceId,
                    completion.ResourceDeploymentId,
                    completion.ResourceRevision,
                    completion.DeploymentGeneration,
                    completion.Operation))
                .FirstOrDefaultAsync(ct);
            if (release is null || completion.Retryable)
                return transaction.Commit(new WebServiceDeploymentCompletionRecorded());

            var replacement = WebServiceReleaseFactory.ApplyCompletion(
                release, completion.ResourceDeploymentId, completion.ResourceRevision,
                completion.DeploymentGeneration, completion.Operation, completion.Succeeded,
                completion.Retryable, completion.FailureCode, completion.FailureMessage,
                completion.CompletedAtUtc);
            if (Equals(release, replacement))
                return transaction.Commit(new WebServiceDeploymentCompletionRecorded());
            var updated = await releases.ReplaceOneAsync(
                transaction.Session,
                WebServiceReleaseFilters.CompletionCas(release.Id, release.Outcome),
                replacement,
                cancellationToken: ct);
            if (updated.ModifiedCount != 1)
                return transaction.Commit(new WebServiceDeploymentCompletionRecorded());

            if (completion.Operation == WebServiceReleaseDeploymentOperation.Delete &&
                completion.Succeeded)
            {
                var readiness = await getExactResourceDeploymentFinalizationReadiness(
                    new GetExactResourceDeploymentFinalizationReadinessRequest(
                        TenantId: tenantId,
                        ResourceType: WebServiceResourceDeployment.ResourceType,
                        ResourceId: webServiceId,
                        ResourceDeploymentId: completion.ResourceDeploymentId),
                    session: transaction.Session,
                    cancellationToken: ct);
                if (readiness == ResourceDeploymentFinalizationReadiness.Ready)
                {
                    var bindings = await deleteHostnameBindingsForDeployment(
                        new DeleteHostnameBindingsForDeploymentRequest(
                            TenantId: tenantId,
                            ResourceType: WebServiceResourceDeployment.ResourceType,
                            ResourceId: webServiceId,
                            ResourceDeploymentId: completion.ResourceDeploymentId),
                        session: transaction.Session,
                        cancellationToken: ct);
                    if (bindings is HostnameBindingCleanupError bindingCleanupError)
                    {
                        return transaction.Abort(new WebServiceFinalizationError(bindingCleanupError));
                    }
                    var purge = await purgeExactDeletedResourceDeployment(
                        new PurgeExactDeletedResourceDeploymentRequest(
                            TenantId: tenantId,
                            ResourceType: WebServiceResourceDeployment.ResourceType,
                            ResourceId: webServiceId,
                            ResourceDeploymentId: completion.ResourceDeploymentId),
                        session: transaction.Session,
                        cancellationToken: ct);
                    if (purge is ResourceDeploymentFinalizationConflict conflict)
                    {
                        return transaction.Abort(new WebServiceFinalizationError(conflict));
                    }
                }
            }

            if (completion.Operation == WebServiceReleaseDeploymentOperation.Apply &&
                completion.Succeeded)
            {
                var current = await webServices.Find(
                        transaction.Session,
                        WebServiceFilters.ById(tenantId, webServiceId))
                    .FirstOrDefaultAsync(ct);
                if (current is { Exposure.Mode: WebServiceExposureMode.Private } &&
                    current.ActiveRevision == completion.ResourceRevision)
                {
                    var bindings = await loadHostnameBindingsForDeployment(
                        new LoadHostnameBindingsForDeploymentInTransactionRequest(
                            TenantId: tenantId,
                            ResourceType: WebServiceResourceDeployment.ResourceType,
                            ResourceId: webServiceId,
                            ResourceDeploymentId: completion.ResourceDeploymentId),
                        session: transaction.Session,
                        cancellationToken: ct);
                    if (bindings.Count > 0)
                    {
                        var fence = await webServices.UpdateOneAsync(
                            transaction.Session,
                            WebServiceFilters.PrivateActiveRevision(
                                tenantId,
                                webServiceId,
                                completion.ResourceRevision),
                            Builders<WebService>.Update.Inc(service => service.Revision, 1),
                            cancellationToken: ct);
                        if (fence.ModifiedCount == 1)
                        {
                            var cleanup = await deleteHostnameBindingsForDeployment(
                                new DeleteHostnameBindingsForDeploymentRequest(
                                    TenantId: tenantId,
                                    ResourceType: WebServiceResourceDeployment.ResourceType,
                                    ResourceId: webServiceId,
                                    ResourceDeploymentId: completion.ResourceDeploymentId),
                                session: transaction.Session,
                                cancellationToken: ct);
                            if (cleanup is HostnameBindingCleanupError cleanupError)
                            {
                                return transaction.Abort(
                                    new WebServiceFinalizationError(cleanupError));
                            }
                        }
                    }
                }
            }

            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(new WebServiceDeploymentCompletedEvent(
                    release.Id,
                    release.WebServiceId,
                    completion.ResourceDeploymentId,
                    completion.ResourceRevision,
                    completion.DeploymentGeneration,
                    completion.Operation.ToString(),
                    completion.Succeeded,
                    completion.Retryable,
                    completion.FailureCode,
                    completion.FailureMessage,
                    replacement.Outcome.ToString(),
                    replacement.Outcome is not WebServiceReleaseOutcome.Pending)
                {
                    EventCore = completion.EventCore
                })],
                ct);
            return transaction.Commit(new WebServiceDeploymentCompletionRecorded());
        }, cancellationToken);
    }
}
