using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Persistence;

public sealed record WebServiceDeploymentCompletion(
    bool Succeeded,
    DateTime CompletedAtUtc,
    string? FailureMessage,
    EventCore EventCore);

public delegate Task<Result> CompleteWebServiceDeployment(
    string tenantId,
    string webServiceId,
    long deploymentGeneration,
    WebServiceDeploymentCompletion completion,
    CancellationToken cancellationToken = default);

internal sealed class MongoCompleteWebServiceDeployment(
    IMongoCollection<WebServiceRelease> releases,
    RunMongoTransaction runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<Result> InvokeAsync(
        string tenantId,
        string webServiceId,
        long deploymentGeneration,
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
            var update = Builders<WebServiceRelease>.Update
                .Set(
                    item => item.Outcome,
                    completion.Succeeded
                        ? WebServiceReleaseOutcome.Succeeded
                        : WebServiceReleaseOutcome.Failed)
                .Set(item => item.CompletedAtUtc, completion.CompletedAtUtc)
                .Set(item => item.FailureMessage, completion.FailureMessage);
            var release = await releases.FindOneAndUpdateAsync(
                session,
                WebServiceReleaseFilters.PendingGeneration(
                    tenantId,
                    webServiceId,
                    deploymentGeneration),
                update,
                new FindOneAndUpdateOptions<WebServiceRelease>
                {
                    ReturnDocument = ReturnDocument.After
                },
                ct);
            if (release is null)
                return Result.Success();

            await storeOutboxWrites(
                session,
                [OutboxWrite.Create(new WebServiceDeploymentCompletedEvent(
                    release.Id,
                    release.WebServiceId,
                    completion.Succeeded,
                    completion.FailureMessage)
                {
                    EventCore = completion.EventCore
                })],
                ct);
            return Result.Success();
        }, cancellationToken);
    }
}
