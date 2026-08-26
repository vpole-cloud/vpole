using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Persistence;

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
    RunMongoOutcomeTransaction<CompleteWebServiceDeploymentOutcome> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites,
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
                    tenantId, WebServiceResourceDeployment.ResourceType, webServiceId,
                    completion.ResourceDeploymentId, transaction.Session, ct);
                if (readiness == ResourceDeploymentFinalizationReadiness.Ready)
                {
                    var bindings = await deleteHostnameBindingsForDeployment(
                        tenantId, WebServiceResourceDeployment.ResourceType, webServiceId,
                        completion.ResourceDeploymentId, transaction.Session, ct);
                    if (!bindings.TryGetValue(out _, out var bindingCleanupError))
                    {
                        return transaction.Abort(new WebServiceFinalizationError(bindingCleanupError));
                    }
                    var purge = await purgeExactDeletedResourceDeployment(
                        tenantId, WebServiceResourceDeployment.ResourceType, webServiceId,
                        completion.ResourceDeploymentId, transaction.Session, ct);
                    if (purge is Err<ResourceDeploymentFinalizationConflict> conflict)
                    {
                        return transaction.Abort(new WebServiceFinalizationError(conflict.Value));
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
