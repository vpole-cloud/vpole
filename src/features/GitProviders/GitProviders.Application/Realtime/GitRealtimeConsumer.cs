using MassTransit;
using Pole.Features.GitProviders.Events;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.GitProviders.Application.Realtime;

public sealed class NotifyGitMetadataChangedConsumer(PublishRealtimeChange publish)
    : IConsumer<GitProviderConnectionChangedEvent>, IConsumer<SourceRepositoryChangedEvent>
{
    public Task HandleAsync(GitProviderConnectionChangedEvent message, CancellationToken ct = default) => publish(new("git", message.EventCore.TenantId), ct);
    public Task HandleAsync(SourceRepositoryChangedEvent message, CancellationToken ct = default) => publish(new("git", message.EventCore.TenantId), ct);
    public Task Consume(ConsumeContext<GitProviderConnectionChangedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
    public Task Consume(ConsumeContext<SourceRepositoryChangedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
}
