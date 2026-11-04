using FluentValidation;
using MongoDB.Bson;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Domain;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints.Sources.Previews;

public sealed record ListLifecyclePreviewRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 100) : IMediatorRequest<ListLifecyclePreviewsOutcome>, ITenantReadOperation;

public union ListLifecyclePreviewsOutcome(
    LifecyclePreviewPage,
    GitSourceNotFound);

public sealed class ListLifecyclePreviewValidator : AbstractValidator<ListLifecyclePreviewRequest>
{
    public ListLifecyclePreviewValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.Page).InclusiveBetween(1, 100000);
        RuleFor(item => item.PageSize).InclusiveBetween(1, 200);
    }
}

public sealed class ListLifecyclePreviewHandler(GitLifecyclePreviews previews, GitRepositorySources sources)
    : IMediatorOutcomeHandler<ListLifecyclePreviewRequest, ListLifecyclePreviewsOutcome>
{
    public async Task<ListLifecyclePreviewsOutcome> HandleAsync(ListLifecyclePreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var source = new GitSourceIdentity(ObjectId.Parse(request.TenantId).ToString(),
            ObjectId.Parse(request.Id).ToString());
        if (await sources.LoadAsync(source.TenantId, source.SourceId, cancellationToken) is null)
            return new GitSourceNotFound();
        var previewsPage = await previews.ListAsync(source, request.Page, request.PageSize, cancellationToken);
        return new LifecyclePreviewPage(previewsPage.Items.Select(item => LifecyclePreviewViews.View(item, 1, 1))
            .ToArray(), previewsPage.Page, previewsPage.PageSize, previewsPage.HasMore);
    }
}

public static class ListLifecyclePreviewEndpoint
{
    public static IEndpointRouteBuilder MapListLifecyclePreviewEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/previews", async (IMediator mediator,
                [AsParameters] ListLifecyclePreviewRequest request,
                CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    LifecyclePreviewPage view => Results.Ok(view),
                    GitSourceNotFound missing => RepositoryHttpMappings.Map(missing),
                })).WithName("ListGitLifecyclePreview");
        return app;
    }
}
