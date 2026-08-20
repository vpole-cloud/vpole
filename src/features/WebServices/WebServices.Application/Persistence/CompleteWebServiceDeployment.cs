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

public delegate Task<Result> CompleteWebServiceDeployment(
    string tenantId,
    string webServiceId,
    WebServiceDeploymentCompletion completion,
    CancellationToken cancellationToken = default);

internal sealed class MongoCompleteWebServiceDeployment(
    IMongoCollection<WebServiceRelease> releases,
    RunMongoTransaction runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites,
    DeleteHostnameBindingsForDeployment deleteHostnameBindingsForDeployment,
    GetExactResourceDeploymentFinalizationReadiness getExactResourceDeploymentFinalizationReadiness,
    PurgeExactDeletedResourceDeploymentLegacy purgeExactDeletedResourceDeployment)
{
    public Task<Result> InvokeAsync(
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

        return runMongoTransaction(async (session, ct) =>
        {
            var release = await releases.Find(
                session,
                WebServiceReleaseFilters.PendingDeployment(
                    tenantId,
                    webServiceId,
                    completion.ResourceDeploymentId,
                    completion.ResourceRevision,
                    completion.DeploymentGeneration,
                    completion.Operation))
                .FirstOrDefaultAsync(ct);
            if (release is null || completion.Retryable)
                return Result.Success();

            var replacement = WebServiceReleaseFactory.ApplyCompletion(
                release, completion.ResourceDeploymentId, completion.ResourceRevision,
                completion.DeploymentGeneration, completion.Operation, completion.Succeeded,
                completion.Retryable, completion.FailureCode, completion.FailureMessage,
                completion.CompletedAtUtc);
            if (Equals(release, replacement))
                return Result.Success();
            var updated = await releases.ReplaceOneAsync(
                session,
                Builders<WebServiceRelease>.Filter.And(
                    Builders<WebServiceRelease>.Filter.Eq(item => item.Id, release.Id),
                    Builders<WebServiceRelease>.Filter.Eq(item => item.Outcome, release.Outcome)),
                replacement,
                cancellationToken: ct);
            if (updated.ModifiedCount != 1)
                return Result.Success();

            if (completion.Operation == WebServiceReleaseDeploymentOperation.Delete &&
                completion.Succeeded)
            {
                var readiness = await getExactResourceDeploymentFinalizationReadiness(
                    tenantId, WebServiceResourceDeployment.ResourceType, webServiceId,
                    completion.ResourceDeploymentId, session, ct);
                if (readiness == ResourceDeploymentFinalizationReadiness.Ready)
                {
                    var bindings = await deleteHostnameBindingsForDeployment(
                        tenantId, WebServiceResourceDeployment.ResourceType, webServiceId,
                        completion.ResourceDeploymentId, session, ct);
                    if (bindings is not ISuccess)
                        return (Failure)bindings;
                    var purge = await purgeExactDeletedResourceDeployment(
                        tenantId, WebServiceResourceDeployment.ResourceType, webServiceId,
                        completion.ResourceDeploymentId, session, ct);
                    if (purge is not ISuccess)
                        return (Failure)purge;
                }
            }

            await storeOutboxWrites(
                session,
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
            return Result.Success();
        }, cancellationToken);
    }
}
