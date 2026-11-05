using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.ResourceGroups.Application.ResourceGroups;

public union PersistUpdatedResourceGroupOutcome(
    ResourceGroup,
    ResourceGroupRevisionConflict);

public delegate Task<PersistUpdatedResourceGroupOutcome>
    PersistUpdatedResourceGroup(
        ResourceGroup current,
        ResourceGroup replacement,
        ResourceGroupUpdatedEventChanges changes,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedResourceGroup(
    IMongoCollection<ResourceGroup> resourceGroups,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<PersistUpdatedResourceGroupOutcome>
        runMongoTransaction,
    IGitOpsMutations git)
{
    public Task<PersistUpdatedResourceGroupOutcome> InvokeAsync(
        ResourceGroup current,
        ResourceGroup replacement,
        ResourceGroupUpdatedEventChanges changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(changes);
        if (!string.Equals(current.Id, replacement.Id, StringComparison.Ordinal) ||
            !string.Equals(current.TenantId, replacement.TenantId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A resource-group replacement must preserve identity and tenant ownership.",
                nameof(replacement));
        }

        return runMongoTransaction(async (transaction, ct) =>
        {
            if (!await git.AuthorizeAsync(
                new AuthorizeGitResourceMutationRequest(
                    TenantId: current.TenantId,
                    Kind: "ResourceGroup",
                    ResourceId: current.Id),
                session: transaction.Session,
                ct: ct))
                return transaction.Abort(new ResourceGroupRevisionConflict(
                    current.Id,
                    ResourceGroupRevisionConflictReason.GitOwnedConfiguration));
            if (replacement.Revision == current.Revision) return transaction.Commit(current);
            if (replacement.Revision != current.Revision)
            {
                var write = await resourceGroups.ReplaceOneAsync(
                    transaction.Session,
                    ResourceGroupFilters.AtRevisionAndRegistrationFence(current),
                    replacement,
                    cancellationToken: ct);
                if (write.ModifiedCount != 1)
                {
                    return transaction.Abort(new ResourceGroupRevisionConflict(
                        current.Id,
                        ResourceGroupRevisionConflictReason.ExpectedRevisionConflict));
                }
            }

            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(new ResourceGroupUpdatedEvent(
                    replacement.Id,
                    replacement.Name,
                    replacement.Description,
                    changes,
                    replacement.Revision)
                {
                    EventCore = EventCore.Create(replacement.TenantId)
                })],
                ct);
            return transaction.Commit(replacement);
        }, cancellationToken);
    }
}
