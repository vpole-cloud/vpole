using Pole.Features.Events.Abstractions;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;

namespace Pole.Features.SourceBuilds.Application.Common;

internal static class SourceBuildConfigurationEventExtensions
{
    public static SourceBuildConfigurationChangedEvent ToChangedEvent(this SourceBuildConfiguration configuration) =>
        new(configuration.Id, configuration.WebServiceId)
        {
            EventCore = EventCore.Create(configuration.TenantId)
        };
}
