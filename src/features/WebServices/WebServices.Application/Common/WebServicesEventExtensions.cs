using System.Collections.Immutable;
using Pole.Features.Events.Abstractions;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Application.Common;

internal static class WebServicesEventExtensions
{
    extension(WebService webService)
    {
        internal WebServiceCreatedEvent ToCreateEvent()
            => new WebServiceCreatedEvent(
                webService.Id,
                webService.Name,
                webService.ResourceGroupId,
                webService.Spec.ToSnapshot(),
                webService.DnsRecord,
                webService.Status,
                webService.Tags
            )
            {
                EventCore = EventCore.Create(webService.TenantId)
            };

        internal WebServiceDeletedEvent ToDeleteEvent()
            => new WebServiceDeletedEvent(
                webService.Id,
                webService.Name,
                webService.ResourceGroupId,
                webService.DnsRecord.Id,
                webService.Spec.ToSnapshot(),
                webService.Tags)
            {
                EventCore = EventCore.Create(webService.TenantId)
            };
    }

    extension(WebServiceRelease release)
    {
        internal WebServiceDeploymentRequestedEvent ToDeploymentRequestedEvent(
            WebService webService,
            EventCore eventCore)
            => new(
                release.Id,
                release.Revision,
                release.Action.ToString(),
                webService.Id,
                webService.Name,
                webService.ResourceGroupId,
                webService.DnsRecord,
                release.Spec)
            {
                EventCore = eventCore
            };
    }

    extension(WebServiceCreatedEvent createEvent)
    {
        internal WebService ToWebService()
            => WebServiceFactory.Create(
                createEvent.Name,
                createEvent.EventCore.TenantId,
                createEvent.ResourceGroupId,
                createEvent.Spec.ToDomain(),
                createEvent.DnsRecord,
                WebServiceFactory.DefaultOptions with
                {
                    Id = createEvent.WebServiceId,
                    Status = createEvent.Status,
                    Tags = createEvent.Tags
                });
    }

    extension(WebServiceDeploymentRequestedEvent deploymentEvent)
    {
        internal WebService ToWebService()
            => WebServiceFactory.Create(
                deploymentEvent.Name,
                deploymentEvent.EventCore.TenantId,
                deploymentEvent.ResourceGroupId,
                deploymentEvent.Spec.ToDomain(),
                deploymentEvent.DnsRecord,
                WebServiceFactory.DefaultOptions with
                {
                    Id = deploymentEvent.WebServiceId,
                    ActiveRevision = deploymentEvent.Revision
                });
    }

    extension(WebServiceSpec spec)
    {
        internal WebServiceSpecSnapshot ToSnapshot()
            => new(
                spec.KubernetesNamespace,
                spec.Image.ToSnapshot(),
                spec.Replicas,
                spec.Resources,
                spec.Config.ToSnapshot(),
                spec.Autoscaling?.ToSnapshot(),
                spec.Probes);
    }

    extension(ImageInfo image)
    {
        private WebServiceImageInfoSnapshot ToSnapshot()
            => new(image.Server, image.Name, image.Tag, image.SecretName);
    }

    extension(WebServiceConfig config)
    {
        private WebServiceConfigSnapshot ToSnapshot()
            => new(
                config.EnvironmentVariables,
                config.PortMappings
                    .Select(mapping => new PortMappingSnapshot(
                        mapping.ServicePort,
                        mapping.TargetPort,
                        mapping.Protocol,
                        mapping.Name))
                    .ToImmutableList());
    }

    extension(AutoscalingSpec autoscaling)
    {
        private AutoscalingSpecSnapshot ToSnapshot()
            => new(
                autoscaling.MinReplicas,
                autoscaling.MaxReplicas,
                new AutoscalingMetricSnapshot(
                    autoscaling.Metric.Type,
                    autoscaling.Metric.TargetAverageUtilization));
    }

    extension(WebServiceSpecSnapshot spec)
    {
        internal WebServiceSpec ToDomain()
            => new(
                spec.KubernetesNamespace,
                spec.Image.ToDomain(),
                spec.Replicas,
                spec.Resources,
                spec.Config.ToDomain(),
                spec.Autoscaling?.ToDomain(),
                spec.Probes);
    }

    extension(WebServiceImageInfoSnapshot image)
    {
        private ImageInfo ToDomain()
            => new(image.Server, image.Name, image.Tag, image.SecretName);
    }

    extension(WebServiceConfigSnapshot config)
    {
        private WebServiceConfig ToDomain()
            => new(
                config.EnvironmentVariables,
                config.PortMappings
                    .Select(mapping => new PortMapping(
                        mapping.ServicePort,
                        mapping.TargetPort,
                        mapping.Protocol,
                        mapping.Name))
                    .ToImmutableList());
    }

    extension(AutoscalingSpecSnapshot autoscaling)
    {
        private AutoscalingSpec ToDomain()
            => new(
                autoscaling.MinReplicas,
                autoscaling.MaxReplicas,
                new AutoscalingMetric(
                    autoscaling.Metric.Type,
                    autoscaling.Metric.TargetAverageUtilization));
    }
}


