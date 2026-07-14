using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Endpoints;

public sealed record DeleteSourceBuildConfigurationRequest(
    [FromRoute] string ConfigurationId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<string>, ITenantWriteOperation;

public sealed class DeleteSourceBuildConfigurationHandler(ISourceBuildDbContext dbContext)
    : IMediatorHandler<DeleteSourceBuildConfigurationRequest, string>
{
    public async Task<Result<string>> HandleAsync(
        DeleteSourceBuildConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var terminalStates = Builders<SourceBuildJob>.Filter.Or(
            Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, SucceededSourceBuild>(item => item.State),
            Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, FailedSourceBuild>(item => item.State));
        var hasActiveBuilds = await dbContext.SourceBuildJobs
            .Find(Builders<SourceBuildJob>.Filter.And(
                Builders<SourceBuildJob>.Filter.Eq(item => item.TenantId, request.TenantId),
                Builders<SourceBuildJob>.Filter.Eq(item => item.ConfigurationId, request.ConfigurationId),
                Builders<SourceBuildJob>.Filter.Not(terminalStates)))
            .AnyAsync(cancellationToken);
        if (hasActiveBuilds)
            return Failures.Conflict("Wait for active builds to finish before deleting this configuration.");

        var deleted = await dbContext.SourceBuildConfigurations.DeleteOneAsync(
            item => item.TenantId == request.TenantId && item.Id == request.ConfigurationId,
            cancellationToken);
        return deleted.DeletedCount == 0
            ? Failures.NotFound("Source build configuration not found.", request.ConfigurationId)
            : request.ConfigurationId;
    }
}

public static class DeleteSourceBuildConfigurationEndpoint
{
    public static IEndpointRouteBuilder MapDeleteSourceBuildConfigurationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{ConfigurationId}", async (IMediator mediator,
                [AsParameters] DeleteSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("DeleteSourceBuildConfiguration")
            .Produces(StatusCodes.Status204NoContent);
        return app;
    }
}
