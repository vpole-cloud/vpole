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
using Pole.Domain.Models;
using Pole.Features.Events.WebServices;
using Pole.Features.WebServices.Common;
using Pole.Features.WebServices.Kubernetes;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Endpoints;

public static class Update
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
    [FromBody] UpdateWebServiceRequestBody Body
) : IMediatorRequest<UpdateWebServiceResponse>;

public sealed record UpdateWebServiceRequestBody(
    string ResourceGroupId,
    WebServiceSpecDto Spec
);

public sealed record UpdateWebServiceResponse(string Id);

public sealed class UpdateWebServiceHandler(
    MongoDbContext dbContext)
    : IMediatorHandler<UpdateWebServiceRequest, UpdateWebServiceResponse>
{
    public async Task<Result<UpdateWebServiceResponse>> HandleAsync(
        UpdateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await dbContext.WebServices
            .Find(ws => ws.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (webService is null)
            return Result.NotFound($"Web service with ID '{request.Id}' not found.");

        return await dbContext.RunInTransactionAsync<UpdateWebServiceResponse>(async (sh, ct) =>
        {
            await dbContext.WebServices.UpdateOneAsync(
                session: sh,
                ws => ws.Id == request.Id,
                Builders<WebService>.Update
                    .Set(ws => ws.Spec, request.Body.Spec.ToDomain(webService.Spec.KubernetesNamespace)),
                cancellationToken: ct);

            await dbContext.StoreEventAsync(new UpdateEvent
            {
                WebServiceId = request.Id
            }, sh, ct);
            
            return new UpdateWebServiceResponse(
                Id: webService.Id
            );
        }, cancellationToken: cancellationToken);
    }
}

public class UpdateWebServiceEventConsumer(
    MongoDbContext dbContext,
    IKubernetes kubernetes)
    : IConsumer<UpdateEvent>
{
    public async Task Consume(ConsumeContext<UpdateEvent> context)
    {
        var ct = context.CancellationToken;
        var webApp = await dbContext.WebServices
            .Find(wa => wa.Id == context.Message.WebServiceId)
            .FirstOrDefaultAsync(ct);
        if (webApp is null) return;

        await WebServiceKubernetesResourceManager.ReplaceAsync(kubernetes, webApp.ToKubernetes(), ct);
        
        await context.Publish(new UpdatedEvent(WebServiceId: webApp.Id), ct);
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
