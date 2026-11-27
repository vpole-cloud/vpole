// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Application.Logs;
using Pole.Features.SourceBuilds.Domain;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Jobs;

public sealed record GetSourceBuildLogsRequest(
    [FromRoute] string BuildJobId,
    [FromQuery] string WebServiceId,
    [FromTenantHeader] string TenantId,
    [FromQuery] long AfterSequence = 0
) : IMediatorRequest<GetSourceBuildLogsOutcome>, ITenantReadOperation;

public sealed record SourceBuildLogEntryDto(
    long Sequence,
    string Stream,
    string Text,
    DateTime RecordedAtUtc
);

public sealed record SourceBuildLogsDto(
    string BuildJobId,
    string WebServiceId,
    string CaptureState,
    IReadOnlyList<SourceBuildLogEntryDto> Entries,
    long NextSequence,
    long LastSequence,
    bool HasMore,
    bool Truncated,
    DateTime? ExpiresAtUtc,
    int CapturedBytes,
    int? MaximumBytes
);

public sealed record SourceBuildLogCursorAhead;

public union GetSourceBuildLogsOutcome(
    SourceBuildLogsDto,
    SourceBuildJobNotFound,
    SourceBuildLogCursorAhead);

public sealed class GetSourceBuildLogsHandler(
    LoadSourceBuildJob loadJob,
    LoadSourceBuildLog loadLog,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<GetSourceBuildLogsRequest, GetSourceBuildLogsOutcome>
{
    public async Task<GetSourceBuildLogsOutcome> HandleAsync(
        GetSourceBuildLogsRequest request,
        CancellationToken cancellationToken = default)
    {
        // Retained jobs remain the authorization identity after service/configuration deletion.
        var job = await loadJob(request.TenantId, request.BuildJobId, cancellationToken);
        if (job is null || !string.Equals(job.WebServiceId, request.WebServiceId, StringComparison.OrdinalIgnoreCase))
            return new SourceBuildJobNotFound(request.BuildJobId);
        var log = await loadLog(job.TenantId, job.WebServiceId, job.Id, cancellationToken);
        if (log is null)
            return request.AfterSequence > 0
                ? new SourceBuildLogCursorAhead()
                : new SourceBuildLogsDto(
                    job.Id,
                    job.WebServiceId,
                    "Unavailable",
                    [],
                    0,
                    0,
                    false,
                    false,
                    null,
                    0,
                    null);
        if (request.AfterSequence > log.LastSequence)
            return new SourceBuildLogCursorAhead();
        var recovered = log.Recover(timeProvider.GetUtcNow().UtcDateTime);
        var entries = new List<SourceBuildLogEntryDto>();
        var bytes = 0;
        foreach (var entry in recovered.Entries.Where(entry => entry.Sequence > request.AfterSequence))
        {
            var size = Encoding.UTF8.GetByteCount(entry.Text);
            if (entries.Count >= 32 || bytes + size > 64 * 1024)
                break;
            entries.Add(
                new SourceBuildLogEntryDto(
                    entry.Sequence,
                    entry.Stream switch
                    {
                        SourceBuildLogStream.System => "system",
                        SourceBuildLogStream.StandardOutput => "stdout",
                        SourceBuildLogStream.StandardError => "stderr",
                        _ => throw new InvalidOperationException("Unknown persisted log stream."),
                    },
                    entry.Text,
                    entry.RecordedAtUtc));
            bytes += size;
        }

        var next = entries.Count > 0 ? entries[^1].Sequence : request.AfterSequence;
        return new SourceBuildLogsDto(
            job.Id,
            job.WebServiceId,
            recovered.State switch
            {
                OpenSourceBuildLog => "Open",
                CompletedSourceBuildLog => "Completed",
                InterruptedSourceBuildLog => "Interrupted",
                ExpiredSourceBuildLog => "Expired",
                UnavailableSourceBuildLog => "Unavailable",
            },
            entries,
            next,
            recovered.LastSequence,
            recovered.State is not ExpiredSourceBuildLog && next < recovered.LastSequence,
            recovered.Truncated,
            recovered.ExpiresAtUtc,
            recovered.CapturedBytes,
            recovered.Limits.MaxBytes);
    }
}

public sealed class GetSourceBuildLogsValidator : AbstractValidator<GetSourceBuildLogsRequest>
{
    public GetSourceBuildLogsValidator()
    {
        RuleFor(request => request.BuildJobId).MongoObjectId("Build job ID");
        RuleFor(request => request.WebServiceId).MongoObjectId("Web service ID");
        RuleFor(request => request.AfterSequence).GreaterThanOrEqualTo(0);
    }
}

public static class GetSourceBuildLogsEndpoint
{
    public static IEndpointRouteBuilder MapGetSourceBuildLogsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{BuildJobId}/logs",
                async (
                    IMediator mediator, [AsParameters] GetSourceBuildLogsRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SourceBuildLogsDto value => Results.Ok(value),
                    SourceBuildJobNotFound error => SourceBuildHttpMappings.MapJobNotFound(error),
                    SourceBuildLogCursorAhead => Results.Problem(
                        statusCode: 400,
                        title: "The log cursor is ahead of the retained capture.",
                        extensions: new Dictionary<string, object?> { ["code"] = "source-builds.log-cursor-ahead" }),
                }))
            .WithName("GetSourceBuildLogs")
            .Produces<SourceBuildLogsDto>()
            .ProducesProblem(404)
            .ProducesProblem(400);
        return app;
    }
}
