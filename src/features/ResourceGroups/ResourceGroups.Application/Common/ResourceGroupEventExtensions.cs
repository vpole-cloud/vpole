using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.ResourceGroups.Application.Common;

internal static class ResourceGroupEventExtensions
{
    extension(ResourceGroup resourceGroup)
    {
        internal ResourceGroupCreatedEvent ToCreateEvent()
            => new(
                resourceGroup.Id,
                resourceGroup.Name,
                resourceGroup.Description,
                resourceGroup.Resources,
                resourceGroup.Tags)
            {
                EventCore = EventCore.Create(resourceGroup.TenantId)
            };

        internal ResourceGroupDeletedEvent ToDeleteEvent()
            => new(
                resourceGroup.Id,
                resourceGroup.Name,
                resourceGroup.Description,
                resourceGroup.Resources,
                resourceGroup.Tags)
            {
                EventCore = EventCore.Create(resourceGroup.TenantId)
            };
    }
}
