using Pole.Features.Events.Abstractions;
using Pole.Features.GitProviders.Domain;
using Pole.Features.GitProviders.Events;

namespace Pole.Features.GitProviders.Application.Common;

internal static class GitMetadataEventExtensions
{
    public static GitProviderConnectionChangedEvent ToChangedEvent(this GitProviderConnection connection) =>
        new(connection.Id)
        {
            EventCore = EventCore.Create(connection.TenantId)
        };

    public static SourceRepositoryChangedEvent ToChangedEvent(this SourceRepository repository) =>
        new(repository.Id, repository.ConnectionId)
        {
            EventCore = EventCore.Create(repository.TenantId)
        };
}
