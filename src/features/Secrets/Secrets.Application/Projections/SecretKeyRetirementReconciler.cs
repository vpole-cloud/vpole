using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Secrets.Application.Projections;

public sealed class SecretKeyRetirementReconciler(
    LoadSecretById loadSecret,
    ReleaseRetainedSecretKeys release,
    IEnumerable<ISecretConsumerRolloutObserver> observers)
{
    public async Task ReconcileAsync(string tenantId, string secretId, CancellationToken ct = default)
    {
        var secret = await loadSecret(tenantId, secretId, ct);
        if (secret is null) return;
        foreach (var binding in secret.Bindings.Where(binding => !binding.RetainedKeys.IsEmpty))
        {
            var observer = observers.SingleOrDefault(item => item.ResourceType.Value == binding.ConsumerType);
            if (observer is null) continue;
            // Native reads deliberately occur before the Mongo transaction. Persistence fences
            // the complete placement set and Secret reference identity before releasing any pin.
            if (await observer.ObserveAsync(tenantId, binding.ConsumerId, ct) is SecretConsumerRolloutConfirmed confirmed)
            {
                if (!await release(tenantId, secretId, secret.Revision, secret.ReferenceFence, binding.Id, confirmed.Deployments, ct))
                    return; // Superseded metadata or proof: retry from a fresh snapshot next time.
                secret = secret.ReleaseRetainedKeys(binding.Id);
            }
        }
    }
}
