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
        internal WebServiceCreatedEvent ToCreateEvent()
            => new WebServiceCreatedEvent(
                webService.Id,
                webService.Name,
                webService.ResourceGroupId,
                webService.Spec.ToSnapshot(),
                webService.DnsRecord.ToSnapshot(),
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

    extension(WebServiceCreatedEvent createEvent)
    {
        internal WebService ToWebService()
            => WebServiceFactory.Create(
                createEvent.Name,
                createEvent.EventCore.TenantId,
                createEvent.ResourceGroupId,
                createEvent.Spec.ToDomain(),
                createEvent.DnsRecord.ToDomain(),
                WebServiceFactory.DefaultOptions with
                {
                    Id = createEvent.WebServiceId,
                    Tags = createEvent.Tags
                });
    }

    extension(WebServiceSpec spec)
    {
        internal WebServiceSpecSnapshot ToSnapshot()
            => new(
                spec.Image.ToSnapshot(),
                spec.Replicas,
                spec.Config.ToSnapshot(),
                spec.Autoscaling?.ToSnapshot(),
                spec.Probes.ToSnapshot());
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
                spec.Image.ToDomain(),
                spec.Replicas,
                spec.Config.ToDomain(),
                spec.Autoscaling?.ToDomain(),
                spec.Probes.ToDomain());
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
                config.EnvironmentVariables ?? ImmutableDictionary<string, string>.Empty,
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

    extension(DnsRecordShortInfo dnsRecord)
    {
        internal WebServiceDnsRecordSnapshot ToSnapshot()
            => new(dnsRecord.Id, dnsRecord.Domain);
    }

    extension(WebServiceDnsRecordSnapshot dnsRecord)
    {
        private DnsRecordShortInfo ToDomain()
            => new(dnsRecord.Id, dnsRecord.Domain);
    }

    extension(ProbeSpec probes)
    {
        private WebServiceProbeSpecSnapshot ToSnapshot()
            => new(
                probes.Startup.ToSnapshot(),
                probes.Liveness.ToSnapshot(),
                probes.Readiness.ToSnapshot());
    }

    extension(ProbeConfig probe)
    {
        private WebServiceProbeConfigSnapshot ToSnapshot()
            => new(
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
        private ProbeSpec ToDomain()
            => new(
                probes.Startup.ToDomain(),
                probes.Liveness.ToDomain(),
                probes.Readiness.ToDomain());
    }

    extension(WebServiceProbeConfigSnapshot probe)
    {
        private ProbeConfig ToDomain()
            => new(
                probe.Path,
                probe.Port,
                probe.InitialDelaySeconds,
                probe.PeriodSeconds,
                probe.TimeoutSeconds,
                probe.FailureThreshold,
                probe.SuccessThreshold);
    }
}
