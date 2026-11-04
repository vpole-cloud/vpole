using System.Text.Json.Serialization;
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

namespace Pole.Features.GitOps.Application.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResumeSnapshotBody(long ExpectedFence);

public sealed record ResumeSnapshotRequest(
    [FromRoute] string Id,
    [FromBody] ResumeSnapshotBody Body,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<ResumeSnapshotOutcome>, ITenantWriteOperation;

public union ResumeSnapshotOutcome(
    SnapshotSourceStatus,
    SnapshotSourceMissing,
    StaleSnapshotConflict);

public sealed class ResumeSnapshotValidator : AbstractValidator<ResumeSnapshotRequest>
{
    public ResumeSnapshotValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.Body).NotNull();
        RuleFor(item => item.Body.ExpectedFence).GreaterThan(0).When(item => item.Body is not null);
    }
}

public sealed class ResumeSnapshotHandler(
    ManualSnapshotSources sources,
    DeclarativePipeline pipeline) : IMediatorOutcomeHandler<ResumeSnapshotRequest, ResumeSnapshotOutcome>
{
    public async Task<ResumeSnapshotOutcome> HandleAsync(
        ResumeSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        var source = await sources.LoadAsync(request.TenantId, request.Id, cancellationToken);

        if (source is null)
            return new SnapshotSourceMissing();

        var execution = await sources.ExecutionAsync(source.TenantId, source.Id, cancellationToken);

        if (execution is null || execution.Fence != request.Body.ExpectedFence)
            return new StaleSnapshotConflict();

        var result = await pipeline.ResumeAsync(execution.ToContext(), cancellationToken);

        if (result.Status == SnapshotApplicationStatus.StaleExecution)
            return new StaleSnapshotConflict();

        return await sources.StatusAsync(source, cancellationToken);
    }
}

public static class ResumeSnapshotEndpoint
{
    public static IEndpointRouteBuilder MapResumeSnapshotEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/resume", async (
                IMediator mediator,
                [AsParameters] ResumeSnapshotRequest request,
                CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SnapshotSourceStatus source => Results.Accepted(value: source),
                    SnapshotSourceMissing error => SnapshotHttpMappings.Map(error),
                    StaleSnapshotConflict error => SnapshotHttpMappings.Map(error)
                })).WithName("ResumeManualSnapshot")
            .WithMetadata(new RequestSizeLimitAttribute(SnapshotHttpLimits.MaximumSourceRequestBytes));

        return app;
    }
}
