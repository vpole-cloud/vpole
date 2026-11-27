// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.Core.Kubernetes;

namespace Pole.Features.RedisServices.Application.Kubernetes;

public sealed record RedisKubernetesApplyFailed(
    string Message
);

public sealed record RedisKubernetesApplied;

public union RedisKubernetesApplyOutcome(RedisKubernetesApplied, RedisKubernetesApplyFailed);

public static class RedisKubernetesResourceManager
{
    public static async Task CreateAsync(
        IKubernetes kubernetes,
        RedisInKubernetes redis,
        CancellationToken cancellationToken = default)
    {
        redis.EnsureRuntimeActive();
        var statefulSet = redis.ToStatefulSet();
        var service = redis.ToService();

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.AppsV1.CreateNamespacedStatefulSetAsync(
                statefulSet,
                redis.Namespace,
                cancellationToken: cancellationToken));

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.CoreV1.CreateNamespacedServiceAsync(
                service,
                redis.Namespace,
                cancellationToken: cancellationToken));
    }

    public static async Task ReplaceAsync(
        IKubernetes kubernetes,
        RedisInKubernetes redis,
        CancellationToken cancellationToken = default)
    {
        redis.EnsureRuntimeActive();
        var statefulSet = redis.ToStatefulSet();
        var service = redis.ToService();

        await kubernetes.AppsV1.ReplaceNamespacedStatefulSetAsync(
            statefulSet,
            statefulSet.Metadata.Name,
            redis.Namespace,
            cancellationToken: cancellationToken);

        await kubernetes.CoreV1.ReplaceNamespacedServiceAsync(
            service,
            service.Metadata.Name,
            redis.Namespace,
            cancellationToken: cancellationToken);
    }

    public static async Task<RedisKubernetesApplyOutcome> ApplyAsync(
        IKubernetes kubernetes,
        RedisInKubernetes redis,
        RedisResourceDeploymentMetadata deploymentMetadata,
        CancellationToken cancellationToken = default)
    {
        redis.EnsureRuntimeActive();
        var stage = "preparing the Redis manifests";
        try
        {
            stage = "preparing the StatefulSet manifest";
            var statefulSet = redis.ToStatefulSet(deploymentMetadata);

            stage = "preparing the Service manifest";
            var service = redis.ToService();

            stage = "applying the StatefulSet";
            await CreateOrReplaceAsync(
                () => kubernetes.AppsV1.CreateNamespacedStatefulSetAsync(
                    statefulSet,
                    redis.Namespace,
                    cancellationToken: cancellationToken),
                async () =>
                {
                    var existing = await kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(
                        statefulSet.Metadata.Name,
                        redis.Namespace,
                        cancellationToken: cancellationToken);
                    statefulSet.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    await kubernetes.AppsV1.ReplaceNamespacedStatefulSetAsync(
                        statefulSet,
                        statefulSet.Metadata.Name,
                        redis.Namespace,
                        cancellationToken: cancellationToken);
                });

            stage = "applying the Service";
            await CreateOrReplaceAsync(
                () => kubernetes.CoreV1.CreateNamespacedServiceAsync(
                    service,
                    redis.Namespace,
                    cancellationToken: cancellationToken),
                async () =>
                {
                    var existing = await kubernetes.CoreV1.ReadNamespacedServiceAsync(
                        service.Metadata.Name,
                        redis.Namespace,
                        cancellationToken: cancellationToken);
                    PreserveServiceOwnedFields(service, existing);
                    await kubernetes.CoreV1.ReplaceNamespacedServiceAsync(
                        service,
                        service.Metadata.Name,
                        redis.Namespace,
                        cancellationToken: cancellationToken);
                });

            return new RedisKubernetesApplied();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new RedisKubernetesApplyFailed($"Unable to apply Redis in Kubernetes while {stage}.");
        }
    }

    public static async Task DeleteAsync(
        IKubernetes kubernetes,
        string redisName,
        string @namespace,
        CancellationToken cancellationToken = default)
    {
        await KubernetesExtensions.DeleteIfExistsAsync(() => kubernetes.AppsV1.DeleteNamespacedStatefulSetAsync(
            name: redisName,
            namespaceParameter: @namespace,
            cancellationToken: cancellationToken));

        await KubernetesExtensions.DeleteIfExistsAsync(() => kubernetes.CoreV1.DeleteNamespacedServiceAsync(
            name: RedisInKubernetes.BuildServiceName(redisName),
            namespaceParameter: @namespace,
            cancellationToken: cancellationToken));
    }

    private static async Task CreateOrReplaceAsync(Func<Task> createCall, Func<Task> replaceCall)
    {
        try
        {
            await createCall();
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            await replaceCall();
        }
    }

    private static void PreserveServiceOwnedFields(V1Service desired, V1Service existing)
    {
        desired.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
        desired.Spec.ClusterIP = existing.Spec.ClusterIP;
        desired.Spec.ClusterIPs = existing.Spec.ClusterIPs;
        desired.Spec.HealthCheckNodePort = existing.Spec.HealthCheckNodePort;
    }
}
