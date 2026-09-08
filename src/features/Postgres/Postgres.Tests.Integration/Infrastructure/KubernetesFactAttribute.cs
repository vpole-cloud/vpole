using k8s;
using Xunit;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

public sealed class KubernetesFactAttribute : FactAttribute
{
    public KubernetesFactAttribute()
    {
        Skip = KubernetesClusterAvailability.SkipReason;
    }
}

internal static class KubernetesClusterAvailability
{
    private static readonly Lazy<string?> CachedSkipReason = new(CheckAvailability);

    public static string? SkipReason => CachedSkipReason.Value;

    private static string? CheckAvailability()
    {
        try
        {
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
            using var client = new Kubernetes(config);

            client.CoreV1
                .ListNamespaceAsync(cancellationToken: cancellationTokenSource.Token)
                .GetAwaiter()
                .GetResult();

            return null;
        }
        catch (Exception ex)
        {
            return $"Kubernetes cluster is unavailable: {ex.GetBaseException().Message}";
        }
    }
}
