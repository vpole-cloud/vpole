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

namespace Pole.Features.Backups.Application.Endpoints;

public sealed record CreateBackupPolicyBody(
    string Name,
    string ResourceType,
    string ResourceId,
    string DestinationId,
    string Schedule,
    bool Suspended,
    int RetentionDays
)
{
    public BackupPolicyInput ToInput() => new(
        Name,
        ResourceType,
        ResourceId,
        DestinationId,
        Schedule,
        Suspended,
        RetentionDays);
}

public sealed record CreateBackupPolicyRequest(
    [FromTenantHeader] string TenantId,
    [FromBody] CreateBackupPolicyBody Body
) : IMediatorRequest<CreateBackupPolicyOutcome>, ITenantCreateOperation;

public union CreateBackupPolicyOutcome(BackupPolicyDto, BackupRequestRejected);

public sealed class CreateBackupPolicyValidator : AbstractValidator<CreateBackupPolicyRequest>
{
    public CreateBackupPolicyValidator()
    {
        RuleFor(r => r.Body).NotNull();
        When(r => r.Body is not null, () => RuleFor(r => r.Body).Must(b => BackupInputRules.Valid(b.ToInput())));
    }
}

public sealed class CreateBackupPolicyHandler(
    ManualBackupConfiguration configuration,
    TimeProvider clock
) : IMediatorOutcomeHandler<CreateBackupPolicyRequest, CreateBackupPolicyOutcome>
{
    public async Task<CreateBackupPolicyOutcome> HandleAsync(
        CreateBackupPolicyRequest request,
        CancellationToken ct = default)
    {
        var input = request.Body.ToInput();
        var answer = await configuration.SavePolicyAsync(
            new(request.TenantId, ObjectId.GenerateNewId().ToString(), 0, input),
            ct);
        return answer switch
        {
            BackupWriteResult<BackupPolicy>.Saved ok => BackupViews.From(ok.Value, clock.GetUtcNow()),
            BackupWriteResult<BackupPolicy>.Rejected failure => new BackupRequestRejected(failure.Code)
        };
    }
}

public static class CreateBackupPolicyEndpoint
{
    public static IEndpointRouteBuilder MapCreateBackupPolicy(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/policies",
                async (
                    IMediator mediator, [AsParameters] CreateBackupPolicyRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupPolicyDto value => Results.Created($"/api/v1/backups/policies/{value.Id}", value),
                    BackupRequestRejected error => BackupHttpProblems.From(error)
                }))
            .WithName("Backups.CreateBackupPolicy");
        return endpoints;
    }
}
