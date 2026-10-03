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
        internal WebServiceCreatedEvent ToCreateEvent(
            ImmutableList<WebServiceDesiredDeploymentSnapshot> deployments) =>
            new(webService.Id, webService.Name, webService.ResourceGroupId, webService.IsolationScopeId,
                webService.Revision, webService.Spec.ToSnapshot(), webService.Exposure.ToSnapshot(),
                webService.Tags, deployments)
            { EventCore = EventCore.Create(webService.TenantId) };

        internal WebServiceDeletedEvent ToDeleteEvent() =>
            new(webService.Id, webService.Name, webService.ResourceGroupId, webService.IsolationScopeId,
                webService.Revision, webService.Spec.ToSnapshot(), webService.Tags)
            { EventCore = EventCore.Create(webService.TenantId) };

        internal WebServiceUpdatedEvent ToUpdatedEvent(
            ImmutableList<WebServiceDesiredDeploymentSnapshot> deployments) =>
            new(webService.Id, webService.Name, webService.ResourceGroupId, webService.IsolationScopeId,
                webService.Revision, webService.Spec.ToSnapshot(), webService.Exposure.ToSnapshot(),
                webService.Tags, deployments)
            { EventCore = EventCore.Create(webService.TenantId) };
    }

    extension(WebServiceCreatedEvent createEvent)
    {
        internal WebService ToWebService() => WebServiceFactory.Create(
            createEvent.Name, createEvent.EventCore.TenantId, createEvent.ResourceGroupId,
            createEvent.IsolationScopeId, createEvent.Spec.ToDomain(),
            WebServiceExposure.Create(
                Enum.Parse<WebServiceExposureMode>(createEvent.Exposure.Mode),
                createEvent.Exposure.GeneratedHostnameLabelPrefix),
            WebServiceFactory.DefaultOptions with
            {
                Id = createEvent.WebServiceId,
                ActiveRevision = createEvent.ResourceRevision,
                Tags = createEvent.Tags
            });
    }

    extension(WebServiceSpec spec)
    {
        internal WebServiceSpecSnapshot ToSnapshot() => new(
            spec.Image.ToSnapshot(), spec.Config.ToSnapshot(), spec.Autoscaling?.ToSnapshot(), spec.Probes.ToSnapshot());
    }

    extension(WebServiceSpecSnapshot spec)
    {
        internal WebServiceSpec ToDomain() => new(
            spec.Image.ToDomain(), spec.Config.ToDomain(), spec.Autoscaling?.ToDomain(), spec.Probes.ToDomain());
    }

    extension(WebServiceExposure exposure)
    {
        private WebServiceExposureSnapshot ToSnapshot() => new(
            exposure.Mode.ToString(), exposure.GeneratedHostnameLabelPrefix);
    }

    extension(ImageInfo image) { private WebServiceImageInfoSnapshot ToSnapshot() => new(image.Server, image.Name, image.Tag, image.SecretName); }
    extension(WebServiceImageInfoSnapshot image) { private ImageInfo ToDomain() => new(image.Server, image.Name, image.Tag, image.SecretName); }
    extension(WebServiceConfig config)
    {
        private WebServiceConfigSnapshot ToSnapshot() => new(config.EnvironmentVariables,
            config.PortMappings.Select(mapping => new PortMappingSnapshot(mapping.ServicePort, mapping.TargetPort, mapping.Protocol, mapping.Name)).ToImmutableList())
        {
            ConnectionEnvironmentReferences = config.ConnectionEnvironmentReferences.ToImmutableDictionary(pair => pair.Key,
                pair => new ConnectionEnvironmentReferenceSnapshot(pair.Value.ConnectionId, pair.Value.ValueKind, pair.Value.ConfigurationKey)),
            SecretEnvironmentReferences = config.SecretEnvironmentReferences.ToImmutableDictionary(pair => pair.Key,
                pair => new SecretEnvironmentReferenceSnapshot(pair.Value.SecretId, pair.Value.Key))
        };
    }
    extension(WebServiceConfigSnapshot config)
    {
        private WebServiceConfig ToDomain() => new(config.EnvironmentVariables ?? ImmutableDictionary<string, string>.Empty,
            config.PortMappings.Select(mapping => new PortMapping(mapping.ServicePort, mapping.TargetPort, mapping.Protocol, mapping.Name)).ToImmutableList())
        {
            ConnectionEnvironmentReferences = (config.ConnectionEnvironmentReferences ?? ImmutableDictionary<string, ConnectionEnvironmentReferenceSnapshot>.Empty).ToImmutableDictionary(pair => pair.Key,
                pair => new ConnectionEnvironmentReference(pair.Value.ConnectionId, pair.Value.ValueKind, pair.Value.ConfigurationKey)),
            SecretEnvironmentReferences = (config.SecretEnvironmentReferences ?? ImmutableDictionary<string, SecretEnvironmentReferenceSnapshot>.Empty).ToImmutableDictionary(pair => pair.Key,
                pair => new SecretEnvironmentReference(pair.Value.SecretId, pair.Value.Key))
        };
    }
    extension(AutoscalingSpec autoscaling) { private AutoscalingSpecSnapshot ToSnapshot() => new(autoscaling.MinReplicas, autoscaling.MaxReplicas, new AutoscalingMetricSnapshot(autoscaling.Metric.Type, autoscaling.Metric.TargetAverageUtilization)); }
    extension(AutoscalingSpecSnapshot autoscaling) { private AutoscalingSpec ToDomain() => new(autoscaling.MinReplicas, autoscaling.MaxReplicas, new AutoscalingMetric(autoscaling.Metric.Type, autoscaling.Metric.TargetAverageUtilization)); }
    extension(ProbeSpec probes) { private WebServiceProbeSpecSnapshot ToSnapshot() => new(probes.Startup.ToSnapshot(), probes.Liveness.ToSnapshot(), probes.Readiness.ToSnapshot()); }
    extension(ProbeConfig probe) { private WebServiceProbeConfigSnapshot ToSnapshot() => new(probe.Path, probe.Port, probe.InitialDelaySeconds, probe.PeriodSeconds, probe.TimeoutSeconds, probe.FailureThreshold, probe.SuccessThreshold); }
    extension(WebServiceProbeSpecSnapshot probes) { private ProbeSpec ToDomain() => new(probes.Startup.ToDomain(), probes.Liveness.ToDomain(), probes.Readiness.ToDomain()); }
    extension(WebServiceProbeConfigSnapshot probe) { private ProbeConfig ToDomain() => new(probe.Path, probe.Port, probe.InitialDelaySeconds, probe.PeriodSeconds, probe.TimeoutSeconds, probe.FailureThreshold, probe.SuccessThreshold); }
}
