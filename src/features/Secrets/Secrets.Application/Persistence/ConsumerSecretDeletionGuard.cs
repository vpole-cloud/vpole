using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed class ConsumerSecretDeletionGuard(
    IMongoCollection<Secret> secrets,
    IGitOpsMutations git,
    SecretPlanningFence fence)
{
    public async Task<EnsureConsumerSecretBindingRemovalOutcome> InvokeAsync(
        EnsureConsumerSecretBindingRemovalRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Consumer Secret binding deletion requires a transaction.");

        var current = await secrets.Find(session, SecretFilters.Consumer(request.TenantId,
            request.ConsumerType.Value, request.ConsumerId)).ToListAsync(cancellationToken);
        var owned = 0;
        foreach (var secret in current)
        {
            foreach (var binding in secret.Bindings.Where(item =>
                         item.Consumer.ResourceType == request.ConsumerType.Value &&
                         item.Consumer.ResourceId == request.ConsumerId))
            {
                if (!await git.AuthorizeAsync(new(request.TenantId, "SecretBinding", binding.Id),
                        session, cancellationToken))
                    owned++;
            }
        }

        if (owned > 0)
            return new ConsumerSecretBindingsRequireRemoval(owned);

        foreach (var secret in current)
        {
            if (!await fence.FenceAsync(secret, session, cancellationToken))
                return new ConsumerSecretBindingDeletionChanged();
        }

        return new ConsumerSecretBindingsRemovable();
    }
}
