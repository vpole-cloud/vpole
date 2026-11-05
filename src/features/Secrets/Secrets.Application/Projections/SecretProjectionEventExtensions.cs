using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application;
using Pole.Features.Events.Abstractions;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Projections;

internal static class SecretProjectionEventExtensions
{
    public static SecretProjectionChangedEvent ToChangedEvent(this SecretProjection projection) =>
        new(projection.SecretId,
            projection.Id,
            projection.ResourceGroupId,
            projection.BindingId,
            projection.DeploymentId,
            projection.DesiredVersion,
            projection.DesiredRevision,
            projection.StorageRevision)
        {
            EventCore = EventCore.Create(projection.TenantId)
        };
}
