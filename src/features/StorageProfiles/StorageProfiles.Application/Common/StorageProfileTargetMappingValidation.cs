using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Common;

internal static class StorageProfileTargetMappingValidation
{
    private const int MaximumConcurrentTargetQueries = 4;

    public static async Task<Result<Dictionary<string, string[]>>> ValidateKnownStorageClassesAsync(
        IReadOnlyCollection<StorageProfileTargetMappingRequestBody> targetMappings,
        IStorageDiscoveryService storageDiscovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetMappings);
        ArgumentNullException.ThrowIfNull(storageDiscovery);

        using var concurrencyGate = new SemaphoreSlim(MaximumConcurrentTargetQueries);
        var checks = targetMappings
            .Select((mapping, index) => CheckAsync(
                index,
                mapping,
                storageDiscovery,
                concurrencyGate,
                cancellationToken))
            .ToArray();
        var outcomes = await Task.WhenAll(checks);

        var failed = outcomes
            .Where(outcome => outcome.Failure is not null)
            .OrderBy(outcome => outcome.Index)
            .FirstOrDefault();
        if (failed?.Failure is { } failure)
            return failure;

        return outcomes
            .Where(outcome => !outcome.IsKnown)
            .OrderBy(outcome => outcome.Index)
            .ToDictionary(
                outcome => $"Body.TargetMappings[{outcome.Index}].StorageClassName",
                outcome => new[]
                {
                    $"Storage class '{outcome.StorageClassName}' was not found in " +
                    $"deployment target '{outcome.DeploymentTargetId}'."
                });
    }

    private static async Task<ValidationOutcome> CheckAsync(
        int index,
        StorageProfileTargetMappingRequestBody mapping,
        IStorageDiscoveryService storageDiscovery,
        SemaphoreSlim concurrencyGate,
        CancellationToken cancellationToken)
    {
        await concurrencyGate.WaitAsync(cancellationToken);
        try
        {
            var deploymentTargetId = mapping.DeploymentTargetId.Trim();
            var storageClassName = mapping.StorageClassName.Trim();
            var discovered = await storageDiscovery.GetStorageClassNamesAsync(
                deploymentTargetId,
                cancellationToken);
            if (!discovered.TryGetValue(out var knownStorageClasses, out var failure))
            {
                return new ValidationOutcome(
                    index,
                    deploymentTargetId,
                    storageClassName,
                    IsKnown: false,
                    failure);
            }

            return new ValidationOutcome(
                index,
                deploymentTargetId,
                storageClassName,
                knownStorageClasses.Contains(storageClassName),
                Failure: null);
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
        Failure? Failure);
}
