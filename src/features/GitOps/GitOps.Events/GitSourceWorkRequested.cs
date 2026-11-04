using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.GitOps.Events;

[EntityName("pole.gitops/v1/source-work-requested")]
public sealed record GitSourceWorkRequested(
    string SourceId,
    EventCore EventCore) : IPoleEvent;
