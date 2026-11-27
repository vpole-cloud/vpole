// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Application.Common;

internal static class WebServicesEventExtensions
{
    extension(WebService webService)
    {
        internal WebServiceCreatedEvent ToCreateEvent(ImmutableList<WebServiceDesiredDeploymentSnapshot> deployments) =>
            new(
                webService.Id,
                webService.Name,
                webService.ResourceGroupId,
                webService.IsolationScopeId,
                webService.Revision,
                webService.Lifecycle.ToSnapshot(),
                webService.Spec.ToSnapshot(),
                webService.Exposure.ToSnapshot(),
                webService.Tags,
                deployments) { EventCore = EventCore.Create(webService.TenantId) };

        internal WebServiceDeletedEvent ToDeleteEvent() => new(
            webService.Id,
            webService.Name,
            webService.ResourceGroupId,
            webService.IsolationScopeId,
            webService.Revision,
            webService.Spec.ToSnapshot(),
            webService.Tags) { EventCore = EventCore.Create(webService.TenantId) };

        internal WebServiceUpdatedEvent
            ToUpdatedEvent(ImmutableList<WebServiceDesiredDeploymentSnapshot> deployments) => new(
            webService.Id,
            webService.Name,
            webService.ResourceGroupId,
            webService.IsolationScopeId,
            webService.Revision,
            webService.Lifecycle.ToSnapshot(),
            webService.Spec.ToSnapshot(),
            webService.Exposure.ToSnapshot(),
            webService.Tags,
            deployments) { EventCore = EventCore.Create(webService.TenantId) };
    }

    extension(WebServiceCreatedEvent createEvent)
    {
        internal WebService ToWebService() => WebServiceFactory.Create(
            createEvent.Name,
            createEvent.EventCore.TenantId,
            createEvent.ResourceGroupId,
            createEvent.IsolationScopeId,
            createEvent.Spec.ToDomain(),
            WebServiceExposure.Create(
                Enum.Parse<WebServiceExposureMode>(createEvent.Exposure.Mode),
                createEvent.Exposure.GeneratedHostnameLabelPrefix),
            WebServiceFactory.DefaultOptions with
            {
                Id = createEvent.WebServiceId,
                ActiveRevision = createEvent.ResourceRevision,
                Tags = createEvent.Tags,
                Lifecycle = createEvent.Lifecycle.ToDomain(),
            });
    }

    extension(WebServiceSpec spec)
    {
        internal WebServiceSpecSnapshot ToSnapshot() => new(
            spec.Image.ToSnapshot(),
            spec.Config.ToSnapshot(),
            spec.Autoscaling?.ToSnapshot(),
            spec.Probes.ToSnapshot());
    }

    extension(WebServiceSpecSnapshot spec)
    {
        internal WebServiceSpec ToDomain() => new(
            spec.Image.ToDomain(),
            spec.Config.ToDomain(),
            spec.Autoscaling?.ToDomain(),
            spec.Probes.ToDomain());
    }

    extension(WebServiceExposure exposure)
    {
        private WebServiceExposureSnapshot ToSnapshot() => new(
            exposure.Mode.ToString(),
            exposure.GeneratedHostnameLabelPrefix);
    }

    private static WebServiceLifecycleState ToSnapshot(this WebServiceLifecycle lifecycle) => lifecycle switch
    {
        PreparedWebServiceLifecycle => WebServiceLifecycleState.Prepared,
        AwaitingFirstArtifactWebServiceLifecycle => WebServiceLifecycleState.AwaitingFirstArtifact,
        ActiveWebServiceLifecycle => WebServiceLifecycleState.Active,
        DeletingWebServiceLifecycle => WebServiceLifecycleState.Deleting,
    };

    private static WebServiceLifecycle ToDomain(this WebServiceLifecycleState lifecycle) => lifecycle switch
    {
        WebServiceLifecycleState.Prepared => new PreparedWebServiceLifecycle(),
        WebServiceLifecycleState.AwaitingFirstArtifact => new AwaitingFirstArtifactWebServiceLifecycle(),
        WebServiceLifecycleState.Active => new ActiveWebServiceLifecycle(),
        _ => throw new InvalidOperationException("A create event cannot represent a deleting service."),
    };

    private static WebServiceImageSnapshot ToSnapshot(this WebServiceImage image) => image switch
    {
        ImageInfo direct => DirectSnapshot(direct),
        UnboundSourceBuildImage => new UnboundSourceBuildImageSnapshot(),
        PendingSourceBuildImage pending => new PendingSourceBuildImageSnapshot(pending.ConfigurationId),
        DeliveredSourceBuildImage delivered => new DeliveredSourceBuildImageSnapshot(
            delivered.ConfigurationId,
            DirectSnapshot(delivered.Image),
            delivered.Digest,
            delivered.JobId,
            delivered.Generation),
    };

    private static WebServiceImageInfoSnapshot DirectSnapshot(ImageInfo image) => new(
        image.Server,
        image.Name,
        image.Tag,
        image.SecretName);

    private static WebServiceImage ToDomain(this WebServiceImageSnapshot image) => image switch
    {
        WebServiceImageInfoSnapshot direct => DirectImage(direct),
        UnboundSourceBuildImageSnapshot => new UnboundSourceBuildImage(),
        PendingSourceBuildImageSnapshot pending => WebServiceImageFactory.Pending(pending.ConfigurationId),
        DeliveredSourceBuildImageSnapshot delivered => DeliveredSourceBuildImage.Create(
            delivered.ConfigurationId,
            DirectImage(delivered.Image),
            delivered.Digest,
            delivered.JobId,
            delivered.Generation),
    };

    private static ImageInfo DirectImage(WebServiceImageInfoSnapshot image) => new(
        image.Server,
        image.Name,
        image.Tag,
        image.SecretName);

    extension(WebServiceConfig config)
    {
        private WebServiceConfigSnapshot ToSnapshot() => new(
            config.EnvironmentVariables,
            config.PortMappings
                .Select(mapping => new PortMappingSnapshot(
                    mapping.ServicePort,
                    mapping.TargetPort,
                    mapping.Protocol,
                    mapping.Name))
                .ToImmutableList())
        {
            ConnectionEnvironmentReferences = config.ConnectionEnvironmentReferences.ToImmutableDictionary(
                pair => pair.Key,
                pair => new ConnectionEnvironmentReferenceSnapshot(
                    pair.Value.ConnectionId,
                    pair.Value.ValueKind,
                    pair.Value.ConfigurationKey)),
            SecretEnvironmentReferences = config.SecretEnvironmentReferences.ToImmutableDictionary(
                pair => pair.Key,
                pair => new SecretEnvironmentReferenceSnapshot(pair.Value.SecretId, pair.Value.Key)),
        };
    }

    extension(WebServiceConfigSnapshot config)
    {
        private WebServiceConfig ToDomain() => new()
        {
            EnvironmentVariables = config.EnvironmentVariables ?? ImmutableDictionary<string, string>.Empty,
            PortMappings = config.PortMappings
                .Select(mapping => new PortMapping(
                    mapping.ServicePort,
                    mapping.TargetPort,
                    mapping.Protocol,
                    mapping.Name))
                .ToImmutableList(),
            ConnectionEnvironmentReferences
                = (config.ConnectionEnvironmentReferences ??
                    ImmutableDictionary<string, ConnectionEnvironmentReferenceSnapshot>.Empty).ToImmutableDictionary(
                    pair => pair.Key,
                    pair => new ConnectionEnvironmentReference(
                        pair.Value.ConnectionId,
                        pair.Value.ValueKind,
                        pair.Value.ConfigurationKey)),
            SecretEnvironmentReferences
                = (config.SecretEnvironmentReferences ??
                    ImmutableDictionary<string, SecretEnvironmentReferenceSnapshot>.Empty).ToImmutableDictionary(
                    pair => pair.Key,
                    pair => new SecretEnvironmentReference(pair.Value.SecretId, pair.Value.Key)),
        };
    }

    extension(AutoscalingSpec autoscaling)
    {
        private AutoscalingSpecSnapshot ToSnapshot() => new(
            autoscaling.MinReplicas,
            autoscaling.MaxReplicas,
            new AutoscalingMetricSnapshot(autoscaling.Metric.Type, autoscaling.Metric.TargetAverageUtilization));
    }

    extension(AutoscalingSpecSnapshot autoscaling)
    {
        private AutoscalingSpec ToDomain() => new(
            autoscaling.MinReplicas,
            autoscaling.MaxReplicas,
            new AutoscalingMetric(autoscaling.Metric.Type, autoscaling.Metric.TargetAverageUtilization));
    }

    extension(ProbeSpec probes)
    {
        private WebServiceProbeSpecSnapshot ToSnapshot() => new(
            probes.Startup.ToSnapshot(),
            probes.Liveness.ToSnapshot(),
            probes.Readiness.ToSnapshot());
    }

    extension(ProbeConfig probe)
    {
        private WebServiceProbeConfigSnapshot ToSnapshot() => new(
            probe.Path,
            probe.Port,
            probe.InitialDelaySeconds,
            probe.PeriodSeconds,
            probe.TimeoutSeconds,
            probe.FailureThreshold,
            probe.SuccessThreshold);
    }

    extension(WebServiceProbeSpecSnapshot probes)
    {
        private ProbeSpec ToDomain() => new(
            probes.Startup.ToDomain(),
            probes.Liveness.ToDomain(),
            probes.Readiness.ToDomain());
    }

    extension(WebServiceProbeConfigSnapshot probe)
    {
        private ProbeConfig ToDomain() => new(
            probe.Path,
            probe.Port,
            probe.InitialDelaySeconds,
            probe.PeriodSeconds,
            probe.TimeoutSeconds,
            probe.FailureThreshold,
            probe.SuccessThreshold);
    }
}
