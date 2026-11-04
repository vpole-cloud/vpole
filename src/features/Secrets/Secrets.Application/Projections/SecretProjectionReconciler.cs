using System.Security.Cryptography;
using System.Text.Json;
using k8s.Models;
using Pole.Encryption;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Projections;

public sealed class SecretProjectionReconciler(
    LoadSecretById loadSecret,
    LoadSecretVersion loadVersion,
    LoadSecretProjections loadProjections,
    LoadResourceDeploymentSet loadDeployments,
    LoadExactResourceDeploymentRuntimePlacement loadPlacement,
    ClaimSecretProjection claim,
    CompleteSecretProjection complete,
    IEnvelopeEncryptionService encryption,
    ISecretProjectionTarget target,
    TimeProvider clock)
{
    public async Task ReconcileAsync(string tenantId, string secretId, CancellationToken ct)
    {
        var secret = await loadSecret(tenantId, secretId, ct);
        var desired = new Dictionary<string, SecretProjection>();
        if (secret is not null)
        {
            foreach (var binding in secret.Bindings.Where(binding =>
                         binding.Usage == Pole.Features.Secrets.Primitives.SecretBindingUsage.Runtime))
            {
                var deployments = await loadDeployments(
                    new LoadResourceDeploymentSetRequest(
                        TenantId: tenantId,
                        ResourceType: ResourceTypeId.Create(binding.Consumer.ResourceType),
                        ResourceId: binding.Consumer.ResourceId),
                    cancellationToken: ct);
                foreach (var deployment in deployments.Where(x => Selected(secret, binding, x)))
                {
                    var placement = await loadPlacement(
                        new LoadExactResourceDeploymentRuntimePlacementRequest(
                            TenantId: tenantId,
                            ResourceType: ResourceTypeId.Create(binding.Consumer.ResourceType),
                            ResourceId: binding.Consumer.ResourceId,
                            DeploymentId: deployment.DeploymentId),
                        cancellationToken: ct);
                    if (placement is null || placement.DesiredOperation == ResourceDeploymentDesiredOperation.Delete || placement.IsolationScopeId != secret.IsolationScopeId)
                        continue;
                    var projection = SecretProjectionFactory.Create(secret, binding, placement.DeploymentId, placement.RuntimeCellId, placement.DeploymentTargetId, placement.KubernetesNamespace);
                    desired.Add(projection.Id, projection);
                }
            }
        }
        foreach (var projection in desired.Values)
            await ReconcileOneAsync(projection, false, ct);
        foreach (var existing in await loadProjections(tenantId, secretId, ct))
            if (!desired.ContainsKey(existing.Id))
                await ReconcileOneAsync(existing, true, ct);
    }

    private static bool Selected(Secret secret, SecretBinding binding, PlacementSummaryDto deployment) =>
        deployment.IsolationScopeId == secret.IsolationScopeId && deployment.DesiredOperation != ResourceDeploymentDesiredOperation.Delete &&
        SecretProjectionAuthority.IncludesDeployment(binding.Selection, deployment.DeploymentId);

    private async Task<SecretBinding?> CurrentBindingAsync(SecretProjection projection, CancellationToken ct)
    {
        var current = await loadSecret(projection.TenantId, projection.SecretId, ct);
        var binding = current?.Bindings.SingleOrDefault(x => x.Id == projection.BindingId);
        if (current is null || binding is null ||
            binding.Usage != Pole.Features.Secrets.Primitives.SecretBindingUsage.Runtime)
            return null;
        if (!SecretProjectionAuthority.MatchesScope(current, projection))
            return null;
        if (!SecretProjectionAuthority.MatchesConsumer(binding, projection))
            return null;
        if (!SecretProjectionAuthority.IncludesDeployment(binding.Selection, projection.DeploymentId))
            return null;
        var placement = await loadPlacement(
            new LoadExactResourceDeploymentRuntimePlacementRequest(
                TenantId: projection.TenantId,
                ResourceType: ResourceTypeId.Create(binding.Consumer.ResourceType),
                ResourceId: binding.Consumer.ResourceId,
                DeploymentId: projection.DeploymentId),
            cancellationToken: ct);
        return placement is not null && SecretProjectionAuthority.MatchesPlacement(placement, projection)
            ? binding : null;
    }

    private async Task ReconcileOneAsync(SecretProjection desired, bool deleting, CancellationToken ct)
    {
        var owned = await claim(desired, Guid.NewGuid().ToString("N"), clock.GetUtcNow().UtcDateTime, ct);
        if (owned is null)
            return;
        var binding = await CurrentBindingAsync(owned, ct);
        if (binding is null)
        {
            await DeleteAndCompleteAsync(owned, ct);
            return;
        }
        // A sweep based on an old snapshot must not delete a still-authorized projection.
        if (deleting)
        {
            await complete(owned.Failed("DesiredStateChanged"), ct);
            return;
        }
        var current = await loadSecret(owned.TenantId, owned.SecretId, ct);
        if (current is null || current.Revision != owned.DesiredRevision)
        {
            await complete(owned.Failed("DesiredStateChanged"), ct);
            return;
        }
        if (current.CurrentVersion == Secret.MetadataOnlyVersion || binding.KeyMappings.Keys.Concat(binding.Pins.ProtectedKeys).Any(key => !current.HasValue(key)))
        {
            await complete(owned.Failed("SecretValuesMissing"), ct);
            return;
        }
        var version = await loadVersion(owned.TenantId, owned.SecretId, owned.DesiredVersion, ct);
        if (version is null)
        {
            await complete(owned.Failed("VersionUnavailable"), ct);
            return;
        }
        var applied = await target.ApplyAsync(owned, async token =>
        {
            var decrypted = await encryption.DecryptAsync(version.Value,
                EnvelopeEncryptionContext.ForTenant(owned.TenantId, "Secret", owned.SecretId, "values", owned.DesiredVersion), token);
            if (decrypted is not byte[] plaintext)
                return new SecretProjectionManifestOutcome(new SecretProjectionDecryptionUnavailable());
            try
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)!;
                return new SecretProjectionManifestOutcome(SecretProjectionManifest.Create(owned, binding, current.DeclaredKeys
                    ? values.Where(pair => current.KeyNames.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value) : values));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }, ct);
        if (applied.Value is SecretProjectionTargetFailure failure)
        {
            await complete(owned.Failed(SecretProjectionTargetErrorCodes.Code(failure)), ct);
            return;
        }
        if (await CurrentBindingAsync(owned, ct) is null)
        {
            await DeleteAndCompleteAsync(owned, ct);
            return;
        }
        var latest = await loadSecret(owned.TenantId, owned.SecretId, ct);
        await complete(latest?.Revision == owned.DesiredRevision ? owned.Applied() : owned.Failed("DesiredStateChanged"), ct);
    }

    private async Task DeleteAndCompleteAsync(SecretProjection owned, CancellationToken ct)
    {
        var deleted = await target.DeleteAsync(owned, ct);
        await complete(deleted.Value switch
        {
            SecretProjectionTargetSucceeded => owned.Deleted(),
            SecretProjectionTargetFailure failure => owned.Failed(SecretProjectionTargetErrorCodes.Code(failure))
        }, ct);
    }
}
