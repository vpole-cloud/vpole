using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;

public delegate Task<Result<ResourceGroup, ResourceGroupRevisionConflict>>
    PersistUpdatedResourceGroup(
        ResourceGroup current,
        ResourceGroup replacement,
        ResourceGroupUpdatedEventChanges changes,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedResourceGroup(
    IMongoCollection<ResourceGroup> resourceGroups,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<Result<ResourceGroup, ResourceGroupRevisionConflict>>
        runMongoTransaction)
{
    public Task<Result<ResourceGroup, ResourceGroupRevisionConflict>> InvokeAsync(
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
            if (replacement.Revision != current.Revision)
            {
                var write = await resourceGroups.ReplaceOneAsync(
                    transaction.Session,
                    ResourceGroupFilters.AtRevision(
                        current.TenantId,
                        current.Id,
                        current.Revision),
                    replacement,
                    cancellationToken: ct);
                if (write.ModifiedCount != 1)
                {
                    return transaction.AbortError<ResourceGroup, ResourceGroupRevisionConflict>(
                        new ResourceGroupRevisionConflict(current.Id));
                }
            }

            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(new ResourceGroupUpdatedEvent(
                    replacement.Id,
                    replacement.Name,
                    replacement.Description,
                    replacement.KubernetesNamespace,
                    replacement.Resources,
                    changes)
                {
                    EventCore = EventCore.Create(replacement.TenantId)
                })],
                ct);
            return transaction.Commit(new Ok<ResourceGroup>(replacement));
        }, cancellationToken);
    }
}
