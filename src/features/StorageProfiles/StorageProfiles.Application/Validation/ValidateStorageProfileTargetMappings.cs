using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Validation;

public sealed record StorageProfileTargetMappingsValid;

public union ValidateStorageProfileTargetMappingsOutcome(
    StorageProfileTargetMappingsValid,
    StorageProfileTargetMappingsInvalid,
    KubernetesStorageDiscoveryUnavailable);

public delegate Task<ValidateStorageProfileTargetMappingsOutcome>
    ValidateStorageProfileTargetMappings(
        StorageProfile profile,
        CancellationToken cancellationToken = default);

internal sealed class KubernetesStorageProfileTargetMappingValidator(
    DiscoverKubernetesStorageClassNames discoverStorageClassNames)
{
    private const int MaximumConcurrentTargetQueries = 4;

    public async Task<ValidateStorageProfileTargetMappingsOutcome> InvokeAsync(
        StorageProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        using var concurrencyGate = new SemaphoreSlim(MaximumConcurrentTargetQueries);
        var checks = profile.TargetMappings
            .Select((mapping, index) => CheckAsync(
                index,
                mapping,
                concurrencyGate,
                cancellationToken))
            .ToArray();
        var outcomes = await Task.WhenAll(checks);

        var unavailable = outcomes
            .Where(outcome => outcome.DiscoveryUnavailable is not null)
            .OrderBy(outcome => outcome.Index)
            .FirstOrDefault();
        if (unavailable?.DiscoveryUnavailable is { } discoveryUnavailable)
        {
            return discoveryUnavailable;
        }

        var errors = outcomes
            .Where(outcome => !outcome.IsKnown)
            .OrderBy(outcome => outcome.Index)
            .ToDictionary(
                outcome => $"Body.TargetMappings[{outcome.Index}].StorageClassName",
                outcome => new[]
                {
                    $"Storage class '{outcome.StorageClassName}' was not found in " +
                    $"deployment target '{outcome.DeploymentTargetId}'."
                });
        return errors.Count == 0
            ? new StorageProfileTargetMappingsValid()
            : new StorageProfileTargetMappingsInvalid(errors);
    }

    private async Task<ValidationOutcome> CheckAsync(
        int index,
        StorageProfileTargetMapping mapping,
        SemaphoreSlim concurrencyGate,
        CancellationToken cancellationToken)
    {
        await concurrencyGate.WaitAsync(cancellationToken);
        try
        {
            var discovered = await discoverStorageClassNames(
                mapping.DeploymentTargetId,
                cancellationToken);
            if (discovered is KubernetesStorageDiscoveryUnavailable error)
            {
                return new ValidationOutcome(
                    index,
                    mapping.DeploymentTargetId,
                    mapping.StorageClassName,
                    IsKnown: false,
                    error);
            }

            var knownStorageClasses = (IReadOnlySet<string>)discovered.Value;
            return new ValidationOutcome(
                index,
                mapping.DeploymentTargetId,
                mapping.StorageClassName,
                knownStorageClasses.Contains(mapping.StorageClassName),
                DiscoveryUnavailable: null);
        }
        finally
        {
            concurrencyGate.Release();
        }
    }

    private sealed record ValidationOutcome(
        int Index,
        string DeploymentTargetId,
        string StorageClassName,
        bool IsKnown,
        KubernetesStorageDiscoveryUnavailable? DiscoveryUnavailable);
}
