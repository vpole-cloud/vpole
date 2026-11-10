using Pole.Features.Events.Abstractions;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Projections;

internal static class SecretProjectionEventExtensions
{
    public static SecretProjectionChangedEvent ToChangedEvent(this SecretProjection projection) => new(
        projection.SecretId,
        projection.Id,
        projection.ResourceGroupId,
        projection.BindingId,
        projection.DeploymentId,
        projection.DesiredVersion,
        projection.DesiredRevision,
        projection.StorageRevision)
    {
        EventCore = EventCore.Create(projection.TenantId),
    };
}
