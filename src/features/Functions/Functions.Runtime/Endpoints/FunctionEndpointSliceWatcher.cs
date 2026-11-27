// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Text.RegularExpressions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pole.Features.Functions.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.Functions.Runtime.Endpoints;

public sealed partial class FunctionEndpointSliceWatcher(
    IFunctionEndpointSliceSource source,
    IFunctionEndpointRegistry registry,
    LoadResourceDeploymentTargetIds loadDeploymentTargetIds,
    TimeProvider timeProvider,
    ILogger<FunctionEndpointSliceWatcher> logger
) : BackgroundService
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanReconnectDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TargetRefreshDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watchers = new Dictionary<string, (CancellationTokenSource Cancellation, Task Task)>(
            StringComparer.Ordinal);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var targetIds = await loadDeploymentTargetIds(FunctionResourceDeployment.ResourceType, stoppingToken);
                var desired = targetIds.ToHashSet(StringComparer.Ordinal);
                foreach (var removed in watchers.Keys.Where(id => !desired.Contains(id)).ToArray())
                {
                    var watcher = watchers[removed];
                    watcher.Cancellation.Cancel();
                    await watcher.Task;
                    watcher.Cancellation.Dispose();
                    registry.RemoveTarget(removed);
                    watchers.Remove(removed);
                }

                foreach (var targetId in desired.Where(id => !watchers.ContainsKey(id)))
                {
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    watchers[targetId] = (cancellation, WatchTargetAsync(targetId, cancellation.Token));
                }

                await Task.Delay(TargetRefreshDelay, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var watcher in watchers.Values)
                watcher.Cancellation.Cancel();
            await Task.WhenAll(watchers.Values.Select(watcher => watcher.Task));
            foreach (var watcher in watchers.Values)
                watcher.Cancellation.Dispose();
        }
    }

    private async Task WatchTargetAsync(string deploymentTargetId, CancellationToken stoppingToken)
    {
        string? resourceVersion = null;
        var retryDelay = InitialRetryDelay;

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                if (resourceVersion is null)
                    resourceVersion = await RelistAsync(deploymentTargetId, stoppingToken);

                var observedEvent = false;
                await foreach (var (type, slice) in source.WatchAsync(
                                       deploymentTargetId,
                                       resourceVersion,
                                       stoppingToken)
                                   .WithCancellation(stoppingToken))
                {
                    observedEvent = true;
                    if (!string.IsNullOrWhiteSpace(slice.Metadata?.ResourceVersion))
                        resourceVersion = slice.Metadata.ResourceVersion;
                    Apply(deploymentTargetId, type, slice);
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
                    "[function-endpoint-watch-relist] EndpointSlice resource version expired for target {DeploymentTargetId}; performing a full relist.",
                    deploymentTargetId);
                resourceVersion = null;
                retryDelay = InitialRetryDelay;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "[function-endpoint-watch-retry] EndpointSlice watch failed for target {DeploymentTargetId}; retrying after {RetryDelayMs}ms.",
                    deploymentTargetId,
                    retryDelay.TotalMilliseconds);
                await Task.Delay(retryDelay, timeProvider, stoppingToken);
                retryDelay = TimeSpan.FromMilliseconds(
                    Math.Min(retryDelay.TotalMilliseconds * 2, MaxRetryDelay.TotalMilliseconds));
            }
    }

    private async Task<string> RelistAsync(string deploymentTargetId, CancellationToken cancellationToken)
    {
        var slices = new List<V1EndpointSlice>();
        string? continuationToken = null;
        string? resourceVersion = null;
        do
        {
            var page = await source.ListPageAsync(deploymentTargetId, continuationToken, cancellationToken);
            slices.AddRange(page.Items.Where(IsFunctionSlice));
            resourceVersion = page.ResourceVersion ?? resourceVersion;
            continuationToken = page.ContinuationToken;
        } while (!string.IsNullOrWhiteSpace(continuationToken));

        if (string.IsNullOrWhiteSpace(resourceVersion))
            throw new InvalidOperationException("Kubernetes EndpointSlice list did not return a resource version.");

        registry.ReplaceTarget(deploymentTargetId, slices);
        return resourceVersion;
    }

    private void Apply(
        string deploymentTargetId,
        WatchEventType type,
        V1EndpointSlice slice)
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
                    registry.ApplySlice(deploymentTargetId, slice);
                else
                    registry.DeleteSlice(deploymentTargetId, @namespace, name);
                break;
            case WatchEventType.Deleted:
                registry.DeleteSlice(deploymentTargetId, @namespace, name);
                break;
        }
    }

    private static bool IsFunctionSlice(V1EndpointSlice slice) => slice.Metadata?.Labels is { } labels &&
        labels.TryGetValue("kubernetes.io/service-name", out var serviceName) &&
        FunctionAgentServiceName().IsMatch(serviceName);

    private static bool IsExpiredResourceVersion(Exception exception) => exception switch
    {
        KubernetesException { Status.Code: 410 } => true,
        HttpOperationException { Response.StatusCode: HttpStatusCode.Gone } => true,
        _ => false,
    };

    [GeneratedRegex("^fn-[a-z0-9]{1,24}-r[1-9][0-9]*-agent$", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionAgentServiceName();
}
