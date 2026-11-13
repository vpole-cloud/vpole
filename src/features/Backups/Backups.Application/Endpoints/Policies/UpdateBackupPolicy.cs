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
using Pole.Scheduling;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Endpoints.Policies;

public sealed record UpdateBackupPolicyBody(
    string Name,
    string ResourceType,
    string ResourceId,
    string DestinationId,
    string Schedule,
    bool Suspended,
    int RetentionDays,
    long ExpectedRevision
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

public sealed record UpdateBackupPolicyRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id,
    [FromBody] UpdateBackupPolicyBody Body
) : IMediatorRequest<UpdateBackupPolicyOutcome>, ITenantWriteOperation;

public union UpdateBackupPolicyOutcome(BackupPolicyDto, BackupRequestRejected);

public sealed class UpdateBackupPolicyValidator : AbstractValidator<UpdateBackupPolicyRequest>
{
    public UpdateBackupPolicyValidator()
    {
        RuleFor(r => r.Body).NotNull();
        When(
            r => r.Body is not null,
            () =>
            {
                RuleFor(r => r.Body.Name)
                    .Must(BackupInputRules.ValidName)
                    .WithMessage("Use a lowercase resource name of at most 63 characters.");
                RuleFor(r => r.Body.ResourceType)
                    .Must(value => ResourceTypeId.TryCreate(value, out _))
                    .WithMessage("Use a valid resource type.");
                RuleFor(r => r.Body.ResourceId).MongoObjectId("Source resource ID");
                RuleFor(r => r.Body.DestinationId).MongoObjectId("Destination ID");
                RuleFor(r => r.Body.Schedule)
                    .MaximumLength(120)
                    .Must(value => UtcCronSchedule.TryParse(value, out _))
                    .WithMessage("Use a five-field UTC cron expression.");
                RuleFor(r => r.Body.RetentionDays).GreaterThan(0);
            });
        RuleFor(r => r.Id).MongoObjectId("Resource ID");
        When(r => r.Body is not null, () => RuleFor(r => r.Body.ExpectedRevision).GreaterThan(0));
    }
}

public sealed class UpdateBackupPolicyHandler(
    ManualBackupConfiguration configuration,
    TimeProvider clock
) : IMediatorOutcomeHandler<UpdateBackupPolicyRequest, UpdateBackupPolicyOutcome>
{
    public async Task<UpdateBackupPolicyOutcome> HandleAsync(
        UpdateBackupPolicyRequest request,
        CancellationToken ct = default)
    {
        var input = request.Body.ToInput();
        var answer = await configuration.SavePolicyAsync(
            new(request.TenantId, request.Id, request.Body.ExpectedRevision, input),
            ct);
        return answer switch
        {
            BackupWriteResult<BackupPolicy>.Saved ok => BackupViews.From(
                ok.Value,
                ok.Value.ReportSchedule(clock.GetUtcNow())),
            BackupWriteResult<BackupPolicy>.Rejected failure => new BackupRequestRejected(failure.Code)
        };
    }
}

public static class UpdateBackupPolicyEndpoint
{
    public static IEndpointRouteBuilder MapUpdateBackupPolicy(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut(
                "/policies/{Id}",
                async (
                    IMediator mediator, [AsParameters] UpdateBackupPolicyRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupPolicyDto value => Results.Ok(value),
                    BackupRequestRejected error => BackupHttpProblems.From(error)
                }))
            .WithName("Backups.UpdateBackupPolicy");
        return endpoints;
    }
}
