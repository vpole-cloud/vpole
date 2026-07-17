using System.Net;
using System.Text.RegularExpressions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pole.Features.Functions.Runtime.Endpoints;

public sealed partial class FunctionEndpointSliceWatcher(
    IFunctionEndpointSliceSource source,
    IFunctionEndpointRegistry registry,
    TimeProvider timeProvider,
    ILogger<FunctionEndpointSliceWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanReconnectDelay = TimeSpan.FromMilliseconds(100);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? resourceVersion = null;
        var retryDelay = InitialRetryDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (resourceVersion is null)
                    resourceVersion = await RelistAsync(stoppingToken);

                var observedEvent = false;
                await foreach (var (type, slice) in source.WatchAsync(resourceVersion, stoppingToken)
                                   .WithCancellation(stoppingToken))
                {
                    observedEvent = true;
                    if (!string.IsNullOrWhiteSpace(slice.Metadata?.ResourceVersion))
                        resourceVersion = slice.Metadata.ResourceVersion;
                    Apply(type, slice);
                }

                if (observedEvent)
                    retryDelay = InitialRetryDelay;
                await Task.Delay(CleanReconnectDelay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (IsExpiredResourceVersion(exception))
            {
                logger.LogInformation(
                    "[function-endpoint-watch-relist] EndpointSlice resource version expired; performing a full relist.");
                resourceVersion = null;
                retryDelay = InitialRetryDelay;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "[function-endpoint-watch-retry] EndpointSlice watch failed; retrying after {RetryDelayMs}ms.",
                    retryDelay.TotalMilliseconds);
                await Task.Delay(retryDelay, timeProvider, stoppingToken);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(
                    retryDelay.TotalMilliseconds * 2,
                    MaxRetryDelay.TotalMilliseconds));
            }
        }
    }

    private async Task<string> RelistAsync(CancellationToken cancellationToken)
    {
        var slices = new List<V1EndpointSlice>();
        string? continuationToken = null;
        string? resourceVersion = null;
        do
        {
            var page = await source.ListPageAsync(continuationToken, cancellationToken);
            slices.AddRange(page.Items.Where(IsFunctionSlice));
            resourceVersion = page.ResourceVersion ?? resourceVersion;
            continuationToken = page.ContinuationToken;
        } while (!string.IsNullOrWhiteSpace(continuationToken));

        if (string.IsNullOrWhiteSpace(resourceVersion))
            throw new InvalidOperationException("Kubernetes EndpointSlice list did not return a resource version.");

        registry.ReplaceAll(slices);
        return resourceVersion;
    }

    private void Apply(WatchEventType type, V1EndpointSlice slice)
    {
        var @namespace = slice.Metadata?.NamespaceProperty;
        var name = slice.Metadata?.Name;
        if (string.IsNullOrWhiteSpace(@namespace) || string.IsNullOrWhiteSpace(name))
            return;

        switch (type)
        {
            case WatchEventType.Added:
            case WatchEventType.Modified:
                if (IsFunctionSlice(slice))
                    registry.ApplySlice(slice);
                else
                    registry.DeleteSlice(@namespace, name);
                break;
            case WatchEventType.Deleted:
                registry.DeleteSlice(@namespace, name);
                break;
        }
    }

    private static bool IsFunctionSlice(V1EndpointSlice slice) =>
        slice.Metadata?.Labels is { } labels &&
        labels.TryGetValue("kubernetes.io/service-name", out var serviceName) &&
        FunctionAgentServiceName().IsMatch(serviceName);

    private static bool IsExpiredResourceVersion(Exception exception) => exception switch
    {
        KubernetesException { Status.Code: 410 } => true,
        HttpOperationException { Response.StatusCode: HttpStatusCode.Gone } => true,
        _ => false
    };

    [GeneratedRegex("^fn-[a-z0-9]{1,24}-r[1-9][0-9]*-agent$", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionAgentServiceName();
}
