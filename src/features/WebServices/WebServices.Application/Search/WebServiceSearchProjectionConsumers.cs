using MassTransit;
using Pole.Features.Search.Contracts;
using Pole.Features.WebServices.Events;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Search;

public static class WebServiceSearchProjection
{
    public static SearchSourceType SourceType { get; } = new(
        ResourceTypeId.Create("pole/web-service"),
        "WebService",
        1);

    public static SearchProjectionSnapshot Snapshot(
        string tenantId,
        string webServiceId,
        string name,
        string resourceGroupId,
        IEnumerable<string> hostnames,
        string image) =>
        new(
            new SearchProjectionIdentity(tenantId, SourceType.ResourceTypeId, webServiceId),
            name,
            resourceGroupId,
            null,
            new[]
                {
                    new SearchProjectionField(SearchProjectionFieldType.SourceId, webServiceId),
                    new SearchProjectionField(SearchProjectionFieldType.Name, name),
                    new SearchProjectionField(SearchProjectionFieldType.Image, image)
                }
                .Concat(hostnames.Distinct().Select(hostname =>
                    new SearchProjectionField(SearchProjectionFieldType.Domain, hostname)))
                .ToArray());
}

public sealed class AddWebServiceToSearchOnWebServiceCreatedConsumer(
    CreateSearchProjection createSearchProjection)
    : IConsumer<WebServiceCreatedEvent>
{
    public Task Handle(WebServiceCreatedEvent message, CancellationToken cancellationToken) =>
        createSearchProjection(Snapshot(message), cancellationToken);

    public Task Consume(ConsumeContext<WebServiceCreatedEvent> context) =>
        Handle(context.Message, context.CancellationToken);

    private static SearchProjectionSnapshot Snapshot(WebServiceCreatedEvent message) =>
        WebServiceSearchProjection.Snapshot(
            message.EventCore.TenantId,
            message.WebServiceId,
            message.Name,
            message.ResourceGroupId,
            message.Deployments.SelectMany(deployment => deployment.Hostnames),
            message.Spec.Image.FullImageName);
}

public sealed class UpdateWebServiceInSearchOnWebServiceUpdatedConsumer(
    UpdateSearchProjection updateSearchProjection)
    : IConsumer<WebServiceUpdatedEvent>
{
    public Task Handle(WebServiceUpdatedEvent message, CancellationToken cancellationToken) =>
        updateSearchProjection(
            WebServiceSearchProjection.Snapshot(
                message.EventCore.TenantId,
                message.WebServiceId,
                message.Name,
                message.ResourceGroupId,
                message.Deployments.SelectMany(deployment => deployment.Hostnames),
                message.Spec.Image.FullImageName),
            cancellationToken);

    public Task Consume(ConsumeContext<WebServiceUpdatedEvent> context) =>
        Handle(context.Message, context.CancellationToken);
}

public sealed class RemoveWebServiceFromSearchOnWebServiceDeletedConsumer(
    DeleteSearchProjection deleteSearchProjection)
    : IConsumer<WebServiceDeletedEvent>
{
    public Task Handle(string tenantId, string webServiceId, CancellationToken cancellationToken) =>
        deleteSearchProjection(
            new SearchProjectionIdentity(
                tenantId,
                WebServiceSearchProjection.SourceType.ResourceTypeId,
                webServiceId),
            cancellationToken);

    public Task Consume(ConsumeContext<WebServiceDeletedEvent> context) =>
        Handle(
            context.Message.EventCore.TenantId,
            context.Message.WebServiceId,
            context.CancellationToken);
}
