using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Execution;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Backups.Application.Endpoints.Operations;

public sealed record RunBackupBody(
    string IdempotencyKey
);

public sealed record RunBackupRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id,
    [FromBody] RunBackupBody Body
) : IMediatorRequest<RunBackupOutcome>, ITenantWriteOperation;

public union RunBackupOutcome(BackupOperationDto, BackupRequestRejected);

public sealed class RunBackupValidator : AbstractValidator<RunBackupRequest>
{
    public RunBackupValidator()
    {
        RuleFor(r => r.Id).MongoObjectId("Source ID");
        RuleFor(r => r.Body).NotNull();
        When(r => r.Body is not null, () => { RuleFor(r => r.Body.IdempotencyKey).NotEmpty().MaximumLength(128); });
    }
}

public sealed class RunBackupHandler(
    BackupOperations commands
) : IMediatorOutcomeHandler<RunBackupRequest, RunBackupOutcome>
{
    public async Task<RunBackupOutcome> HandleAsync(RunBackupRequest request, CancellationToken ct = default)
    {
        var answer = await commands.StartAsync(
            new(request.TenantId, request.Body.IdempotencyKey, request.Id, false, null, null, null),
            ct);
        return answer switch
        {
            BackupWriteResult<BackupOperation>.Saved ok => BackupViews.From(ok.Value),
            BackupWriteResult<BackupOperation>.Rejected failure => new BackupRequestRejected(failure.Code)
        };
    }
}

public static class RunBackupEndpoint
{
    public static IEndpointRouteBuilder MapRunBackup(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/policies/{Id}/runs",
                async (
                    IMediator mediator, [AsParameters] RunBackupRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupOperationDto value => Results.Accepted($"/api/v1/backups/operations/{value.Id}", value),
                    BackupRequestRejected error => BackupHttpProblems.From(error)
                }))
            .WithName("Backups.RunBackup");
        return endpoints;
    }
}
