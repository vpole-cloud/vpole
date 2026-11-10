using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.GitProviders.Events;

[EntityName("pole.git-providers/v1/source-repository-changed")]
public sealed record SourceRepositoryChangedEvent(
    string SourceRepositoryId,
    string ConnectionId,
    string TechnicalName,
    long Revision,
    EventCore EventCore
) : IPoleEvent;
