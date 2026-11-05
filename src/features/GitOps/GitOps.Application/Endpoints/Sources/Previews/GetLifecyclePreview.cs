using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints.Sources.Previews;

public sealed record GetLifecyclePreviewRequest(
    [FromRoute] string Id,
    [FromRoute] string PreviewId,
    [FromTenantHeader] string TenantId,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 100) : IMediatorRequest<GetLifecyclePreviewOutcome>, ITenantReadOperation;

public union GetLifecyclePreviewOutcome(
    LifecyclePreviewView,
    GitSourceNotFound,
    GitPreviewNotFound);

public sealed class GetLifecyclePreviewValidator : AbstractValidator<GetLifecyclePreviewRequest>
{
    public GetLifecyclePreviewValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.PreviewId).MongoObjectId("Preview ID");
        RuleFor(item => item.Page).InclusiveBetween(1, 100000);
        RuleFor(item => item.PageSize).InclusiveBetween(1, 200);
    }
}

public sealed class GetLifecyclePreviewHandler(GitLifecyclePreviews previews, GitRepositorySources sources)
    : IMediatorOutcomeHandler<GetLifecyclePreviewRequest, GetLifecyclePreviewOutcome>
{
    public async Task<GetLifecyclePreviewOutcome> HandleAsync(GetLifecyclePreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var source = new GitSourceIdentity(ObjectId.Parse(request.TenantId).ToString(),
            ObjectId.Parse(request.Id).ToString());
        if (await sources.LoadAsync(source.TenantId, source.SourceId, cancellationToken) is null)
            return new GitSourceNotFound();
        var preview = await previews.LoadAsync(source, ObjectId.Parse(request.PreviewId).ToString(), cancellationToken);
        return preview is null ? new GitPreviewNotFound() : LifecyclePreviewViews.View(preview, request.Page,
            request.PageSize);
    }
}

public static class GetLifecyclePreviewEndpoint
{
    public static IEndpointRouteBuilder MapGetLifecyclePreviewEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/previews/{PreviewId}", async (IMediator mediator,
                [AsParameters] GetLifecyclePreviewRequest request,
                CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    LifecyclePreviewView view => Results.Ok(view),
                    GitSourceNotFound missing => RepositoryHttpMappings.Map(missing),
                    GitPreviewNotFound missing => LifecycleHttpMappings.Map(missing),
                })).WithName("GetGitLifecyclePreview");
        return app;
    }
}
