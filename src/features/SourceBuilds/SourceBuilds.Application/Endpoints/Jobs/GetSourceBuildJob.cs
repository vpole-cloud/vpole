using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Persistence.Jobs;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Jobs;

public sealed record GetSourceBuildJobRequest(
    [FromRoute] string BuildJobId,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetSourceBuildJobOutcome>, ITenantReadOperation;

public union GetSourceBuildJobOutcome(
    SourceBuildJobDto,
    SourceBuildJobNotFound);

public sealed class GetSourceBuildJobHandler(LoadSourceBuildJob loadSourceBuildJob)
    : IMediatorOutcomeHandler<GetSourceBuildJobRequest, GetSourceBuildJobOutcome>
{
    public async Task<GetSourceBuildJobOutcome> HandleAsync(
        GetSourceBuildJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var job = await loadSourceBuildJob(
            request.TenantId,
            request.BuildJobId,
            cancellationToken);
        return job is null
            ? new SourceBuildJobNotFound(request.BuildJobId)
            : job.ToDto();
    }
}

public sealed class GetSourceBuildJobRequestValidator
    : AbstractValidator<GetSourceBuildJobRequest>
{
    public GetSourceBuildJobRequestValidator() =>
        RuleFor(item => item.BuildJobId).MongoObjectId("Source build job ID");
}

public static class GetSourceBuildJobEndpoint
{
    public static IEndpointRouteBuilder MapGetSourceBuildJobEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{BuildJobId}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] GetSourceBuildJobRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    SourceBuildJobDto job => Results.Ok(job),
                    SourceBuildJobNotFound error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("GetSourceBuildJob")
            .Produces<SourceBuildJobDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }
}
