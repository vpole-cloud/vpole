using System.Collections.Immutable;
using FluentValidation;
using k8s;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class UpdateWebService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapUpdateWebServiceEndpoint()
        {
            app.MapPut("/{Id}", async (IMediator mediator, [AsParameters] UpdateWebServiceRequest request, CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(x => Results.Accepted(value: x));
                })
                .WithName("UpdateWebService")
                .Produces<UpdateWebServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed record UpdateWebServiceRequest(
    [FromRoute] string Id,
    [FromBody] UpdateWebServiceRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null
) : IMediatorRequest<UpdateWebServiceResponse>, ITenantWriteOperation;

public sealed record UpdateWebServiceRequestBody(
    string ResourceGroupId,
    WebServiceSpecDto Spec,
    ImmutableHashSet<string> Tags
);

public sealed record UpdateWebServiceResponse(string Id);

public sealed class UpdateWebServiceHandler(
    IWebServiceDbContext dbContext)
    : IMediatorHandler<UpdateWebServiceRequest, UpdateWebServiceResponse>
{
    public async Task<Result<UpdateWebServiceResponse>> HandleAsync(
        UpdateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<WebService>.Filter.And(
            Builders<WebService>.Filter.ForTenant(request.TenantId),
            Builders<WebService>.Filter.Eq(ws => ws.Id, request.Id));

        var webService = await dbContext.WebServices
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (webService is null)
            return Result.NotFound($"Web service with ID '{request.Id}' not found.");

        return await dbContext.RunInTransactionAsync<UpdateWebServiceResponse>(async (sh, ct) =>
        {
            var changes = GetChanges(webService, request.Body);
            if (HasChanges(changes))
            {
                await dbContext.WebServices.UpdateOneAsync(
                    session: sh,
                    filter,
                    GetUpdateDefinition(changes),
                    cancellationToken: ct);
            }

            await dbContext.StoreEventAsync(new WebServiceUpdatedEvent(
                    request.Id,
                    webService.Name,
                    webService.ResourceGroupId,
                    webService.DnsRecord,
                    changes)
                {
                    EventCore = EventCore.Create(webService.TenantId)
                }
                , sh, ct);
            
            return new UpdateWebServiceResponse(
                Id: webService.Id
            );
        }, cancellationToken: cancellationToken);
    }

    private static WebServiceUpdatedEventChanges GetChanges(WebService webService, UpdateWebServiceRequestBody body) =>
        new(UpdateLog<WebServiceSpecSnapshot>.Of(
                webService.Spec.ToSnapshot(),
                body.Spec.ToDomain(webService.Spec.KubernetesNamespace).ToSnapshot()),
            UpdateLog<ImmutableHashSet<string>>.Of(webService.Tags,
                body.Tags));

    private static UpdateDefinition<WebService> GetUpdateDefinition(
        WebServiceUpdatedEventChanges changes)
    {
        var updates = new Queue<UpdateDefinition<WebService>>();
        if (!changes.Tags.AreEqual)
            updates.Enqueue(Builders<WebService>.Update.Set(ws => ws.Tags, changes.Tags.New));
        if (!changes.WebServiceSpec.AreEqual)
            updates.Enqueue(Builders<WebService>.Update.Set(ws => ws.Spec, changes.WebServiceSpec.New.ToDomain()));
        return Builders<WebService>.Update.Combine(updates);
    }

    private static bool HasChanges(WebServiceUpdatedEventChanges changes) =>
        !changes.WebServiceSpec.AreEqual || !changes.Tags.AreEqual;
}

public class UpdateWebServiceEventConsumer(
    IWebServiceDbContext dbContext,
    IKubernetes kubernetes)
    : IConsumer<WebServiceUpdatedEvent>
{
    public async Task Consume(ConsumeContext<WebServiceUpdatedEvent> context)
    {
        var ct = context.CancellationToken;
        var webApp = await dbContext.WebServices
            .Find(wa => wa.Id == context.Message.WebServiceId && wa.TenantId == context.Message.EventCore.TenantId)
            .FirstOrDefaultAsync(ct);
        if (webApp is null) return;

        await WebServiceKubernetesResourceManager.ReplaceAsync(kubernetes, webApp.ToKubernetes(), ct);
    }
}

public sealed class UpdateWebServiceValidator : AbstractValidator<UpdateWebServiceRequest>
{
    public UpdateWebServiceValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty().WithMessage("Web service ID is required.");

        RuleFor(x => x.Body.ResourceGroupId)
            .NotEmpty().WithMessage("Resource group ID is required.");

        RuleFor(x => x.Body.Spec)
            .NotNull().WithMessage("Web service specification is required.")
            .SetValidator(new WebServiceSpecDtoValidator());
    }
}


