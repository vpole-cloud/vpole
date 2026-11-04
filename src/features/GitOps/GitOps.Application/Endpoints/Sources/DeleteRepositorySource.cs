using MongoDB.Bson;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

public sealed record DeleteRepositorySourceRequest(
    [FromRoute] string Id,
    [FromQuery] long ExpectedRevision,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<DeleteRepositorySourceOutcome>, ITenantWriteOperation;

public union DeleteRepositorySourceOutcome(
    GitSourceDeletionRecorded,
    GitSourceNotFound,
    GitSourceRevisionConflict,
    GitSourceDetachRequired,
    GitSourceBusy);

public sealed class DeleteRepositorySourceValidator : AbstractValidator<DeleteRepositorySourceRequest>
{
    public DeleteRepositorySourceValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.ExpectedRevision).GreaterThan(0);
    }
}

public sealed class DeleteRepositorySourceHandler(GitSourceLifecycleStore lifecycle)
    : IMediatorOutcomeHandler<DeleteRepositorySourceRequest, DeleteRepositorySourceOutcome>
{
    public async Task<DeleteRepositorySourceOutcome> HandleAsync(
        DeleteRepositorySourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var outcome = await lifecycle.DeleteAsync(new(ObjectId.Parse(request.TenantId).ToString(),
            ObjectId.Parse(request.Id).ToString(), request.ExpectedRevision), cancellationToken);
        return outcome switch
        {
            GitSourceDeletionRecorded deleted => deleted,
            GitSourceNotFound missing => missing,
            GitSourceRevisionConflict stale => stale,
            GitSourceDetachRequired owned => owned,
            GitSourceBusy busy => busy
        };
    }
}

public static class DeleteRepositorySourceEndpoint
{
    public static IEndpointRouteBuilder MapDeleteRepositorySourceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}",
                async (IMediator mediator, [AsParameters] DeleteRepositorySourceRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                    {
                        GitSourceDeletionRecorded => Results.NoContent(),
                        GitSourceNotFound missing => RepositoryHttpMappings.Map(missing),
                        GitSourceRevisionConflict stale => RepositoryHttpMappings.Map(stale),
                        GitSourceDetachRequired owned => RepositoryHttpMappings.Map(owned),
                        GitSourceBusy busy => RepositoryHttpMappings.Map(busy)
                    })).WithName("DeleteGitRepositorySource");
        return app;
    }
}
