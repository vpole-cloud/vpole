using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Application.Policies;
using Pole.Features.Backups.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Backups.Application.Common.BackupArchiveHttpMappings;
using static Pole.Features.Backups.Application.Common.BackupOperationHttpMappings;
using static Pole.Features.Backups.Application.Common.BackupPolicyHttpMappings;

namespace Pole.Features.Backups.Application.Endpoints.Operations;

public sealed record RunBackupBody(
    string IdempotencyKey
);

public sealed record RunBackupRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id,
    [FromBody] RunBackupBody Body
) : IMediatorRequest<RunBackupOutcome>, ITenantWriteOperation;

public union RunBackupOutcome(
    BackupOperationDto,
    BackupPolicyMissing,
    BackupPolicyStopping,
    BackupArchiveMissing,
    BackupPolicyChangedDuringBackupRequest,
    BackupCommandIdempotencyConflict);

public sealed class RunBackupValidator : AbstractValidator<RunBackupRequest>
{
    public RunBackupValidator()
    {
        RuleFor(r => r.Id).MongoObjectId("Policy ID");
        RuleFor(r => r.Body).NotNull();
        When(r => r.Body is not null, () => { RuleFor(r => r.Body.IdempotencyKey).NotEmpty().MaximumLength(128); });
    }
}

public sealed class RunBackupHandler(
    RequestManualBackup commands
) : IMediatorOutcomeHandler<RunBackupRequest, RunBackupOutcome>
{
    public async Task<RunBackupOutcome> HandleAsync(RunBackupRequest request, CancellationToken ct = default)
    {
        var answer = await commands.ExecuteAsync(
            new ManualBackupCommand(request.TenantId, request.Body.IdempotencyKey, request.Id),
            ct);
        return answer switch
        {
            BackupOperation operation => BackupViews.From(operation),
            BackupPolicyMissing error => error,
            BackupPolicyStopping error => error,
            BackupArchiveMissing error => error,
            BackupPolicyChangedDuringBackupRequest error => error,
            BackupCommandIdempotencyConflict error => error,
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
                    BackupPolicyMissing error => MapPolicyMissing(error),
                    BackupPolicyStopping error => MapPolicyStopping(error),
                    BackupArchiveMissing error => MapArchiveMissing(error),
                    BackupPolicyChangedDuringBackupRequest error => MapPolicyChangedDuringBackupRequest(error),
                    BackupCommandIdempotencyConflict error => MapCommandIdempotencyConflict(error),
                }))
            .WithName("Backups.RunBackup");
        return endpoints;
    }
}
