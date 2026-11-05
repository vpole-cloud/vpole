using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Database;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Bindings;

public delegate Task RemoveConsumerSecretBindings(
    string tenantId,
    string groupId,
    string resourceType,
    string resourceId,
    CancellationToken ct);

internal sealed class MongoRemoveConsumerSecretBindings(
    IMongoCollection<Secret> secrets,
    MongoRunTransaction transactions,
    StoreSecretChange publish,
    IGitOpsMutations git,
    TimeProvider clock)
{
    public async Task InvokeAsync(
        string tenantId,
        string groupId,
        string resourceType,
        string resourceId,
        CancellationToken ct)
    {
        resourceType = ResourceTypeId.Create(resourceType).Value;
        resourceId = SecretIdentity.ObjectId(resourceId);
        var filter = SecretFilters.Consumer(tenantId, groupId, resourceType, resourceId);
        var candidates = await secrets.Find(filter).ToListAsync(ct);
        foreach (var candidate in candidates)
            await transactions.InvokeOutcomeAsync<Unit>(async (tx, token) =>
            {
                var current = await secrets
                    .Find(tx.Session, SecretFilters.Identity(tenantId, candidate.Id))
                    .FirstOrDefaultAsync(token);
                if (current is null)
                    return tx.Commit(Unit.Value);

                var removed = current.Bindings
                    .Where(x => x.Consumer.ResourceType == resourceType && x.Consumer.ResourceId == resourceId)
                    .ToArray();
                if (removed.Length == 0)
                    return tx.Commit(Unit.Value);

                var removable = new List<SecretBinding>();
                foreach (var binding in removed)
                {
                    if (await git.AuthorizeAsync(new(tenantId, "SecretBinding", binding.Id), tx.Session, token))
                        removable.Add(binding);
                }
                if (removable.Count == 0)
                    return tx.Commit(Unit.Value);

                var updated = current;
                foreach (var binding in removable)
                    updated = updated.Detach(binding.Id, clock.GetUtcNow().UtcDateTime);
                var result = await secrets.ReplaceOneAsync(
                    tx.Session,
                    SecretFilters.AtPlanningVersion(current),
                    updated,
                    cancellationToken: token);
                if (result.ModifiedCount != 1)
                    throw new InvalidOperationException("Consumer Secret binding cleanup changed concurrently.");

                foreach (var binding in removable)
                    await publish(tx.Session, updated, new SecretBindingRemoved(binding.Id), token);
                return tx.Commit(Unit.Value);
            }, ct);
    }
}
