using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.SourceBuilds.Events;

[EntityName("pole.source-builds/v1/source-build-configuration-changed")]
public sealed record SourceBuildConfigurationChangedEvent(
    string ConfigurationId,
    string WebServiceId) : IPoleEvent
{
    public EventCore EventCore
    {
        get;
        init;
    } = EventCore.Create();
}
