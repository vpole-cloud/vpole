using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Functions.Application.Revisions;

public sealed record FunctionAppRevisionChanged(
    string FunctionAppId
);

public union PersistFunctionAppRevisionOutcome(
    FunctionRevision,
    FunctionAppRevisionChanged,
    FunctionAppGitOwnedConfiguration);

public sealed record PersistFunctionAppRevisionRequest(
    FunctionApp Current,
    FunctionRevision Candidate,
    FunctionQuotaAllocation AdmittedQuota,
    ImmutableHashSet<string> Tags,
    DateTimeOffset UpdatedAtUtc,
    GitMutationContext? Context
);

public delegate Task<PersistFunctionAppRevisionOutcome> PersistFunctionAppRevision(
    PersistFunctionAppRevisionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

internal sealed class MongoPersistFunctionAppRevision(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions,
    StoreOutboxWrites storeOutboxWrites,
    IGitOpsMutations git
)
{
    public async Task<PersistFunctionAppRevisionOutcome> InvokeAsync(
        PersistFunctionAppRevisionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var (current, candidate, admittedQuota, tags, updatedAtUtc, context) = request;
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Function revision persistence requires an active transaction.");

        if (!await git.AuthorizeAsync(
                new AuthorizeGitResourceMutationRequest(current.TenantId, "FunctionApp", current.Id, context),
                session,
                cancellationToken))
            return new FunctionAppGitOwnedConfiguration(current.Id);

        var update = Builders<FunctionApp>.Update
            .Set(app => app.PendingRevisionId, candidate.Id)
            .Set(app => app.DesiredState, FunctionAppDesiredState.Enabled)
            .Set(app => app.NextRevisionNumber, checked(current.NextRevisionNumber + 1))
            .Set(
                app => app.Status,
                current.ActiveRevisionId is null ? FunctionAppStatus.Provisioning : FunctionAppStatus.Updating)
            .Set(app => app.AdmittedQuota, admittedQuota)
            .Set(app => app.Tags, tags)
            .Set(app => app.UpdatedAtUtc, updatedAtUtc);
        var updated = await functionApps.UpdateOneAsync(
            session,
            FunctionAppFilters.ForPendingRevision(current),
            update,
            cancellationToken: cancellationToken);
        if (updated.MatchedCount != 1)
            return new FunctionAppRevisionChanged(current.Id);

        await functionRevisions.InsertOneAsync(session, candidate, cancellationToken: cancellationToken);
        await storeOutboxWrites(
            session,
            [
                .. current.DesiredState == FunctionAppDesiredState.Prepared
                    ? new[] { OutboxWrite.Create(current.ToCreatedEvent(candidate)) }
                    : [],
                OutboxWrite.Create(candidate.ToRevisionCreatedEvent(current)),
            ],
            cancellationToken);
        return candidate;
    }
}
