using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.ResourceGroups.Common;

internal static class ResourceGroupEventExtensions
{
    extension(ResourceGroup resourceGroup)
    {
        internal CreateEvent ToCreateEvent()
            => new(
                resourceGroup.Id,
                resourceGroup.Name,
                resourceGroup.Description,
                resourceGroup.KubernetesNamespace,
                resourceGroup.Resources,
                resourceGroup.Tags);

        internal DeleteEvent ToDeleteEvent()
            => new(
                resourceGroup.Id,
                resourceGroup.Name,
                resourceGroup.Description,
                resourceGroup.KubernetesNamespace,
                resourceGroup.Resources,
                resourceGroup.Tags);
    }
}
