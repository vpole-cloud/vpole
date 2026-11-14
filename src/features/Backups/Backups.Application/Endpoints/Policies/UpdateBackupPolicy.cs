using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Policies;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Scheduling;
using Pole.Validation;
using Pole.Workloads;
using static Pole.Features.Backups.Application.Common.BackupPolicyHttpMappings;

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

public union UpdateBackupPolicyOutcome(
    BackupPolicyDto,
    BackupSourceUnavailable,
    BackupSourceUnsupported,
    BackupSourcePlacementUnavailable,
    BackupSourceRevisionChanged,
    BackupDestinationPlacementUnavailable,
    BackupRetentionInvalid,
    BackupPolicyDestinationUnavailable,
    BackupPolicyMissing,
    BackupPolicyRevisionConflict,
    BackupPolicyIdentityImmutable,
    BackupPolicyStopping,
    BackupPolicyGitOwned);

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
    BackupPolicyWrites writes,
    TimeProvider clock
) : IMediatorOutcomeHandler<UpdateBackupPolicyRequest, UpdateBackupPolicyOutcome>
{
    public async Task<UpdateBackupPolicyOutcome> HandleAsync(
        UpdateBackupPolicyRequest request,
        CancellationToken ct = default)
    {
        var input = request.Body.ToInput();
        var answer = await writes.UpdateAsync(
            new(request.TenantId, request.Id, request.Body.ExpectedRevision, input),
            ct);
        return answer switch
        {
            BackupPolicy policy => BackupViews.From(policy, policy.ReportSchedule(clock.GetUtcNow())),
            BackupSourceUnavailable error => error,
            BackupSourceUnsupported error => error,
            BackupSourcePlacementUnavailable error => error,
            BackupSourceRevisionChanged error => error,
            BackupDestinationPlacementUnavailable error => error,
            BackupRetentionInvalid error => error,
            BackupPolicyDestinationUnavailable error => error,
            BackupPolicyMissing error => error,
            BackupPolicyRevisionConflict error => error,
            BackupPolicyIdentityImmutable error => error,
            BackupPolicyStopping error => error,
            BackupPolicyGitOwned error => error,
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
                    BackupSourceUnavailable error => MapSourceUnavailable(error),
                    BackupSourceUnsupported error => MapSourceUnsupported(error),
                    BackupSourcePlacementUnavailable error => MapSourcePlacementUnavailable(error),
                    BackupSourceRevisionChanged error => MapSourceRevisionChanged(error),
                    BackupDestinationPlacementUnavailable error => MapDestinationPlacementUnavailable(error),
                    BackupRetentionInvalid error => MapRetentionInvalid(error),
                    BackupPolicyDestinationUnavailable error => MapDestinationUnavailable(error),
                    BackupPolicyMissing error => MapPolicyMissing(error),
                    BackupPolicyRevisionConflict error => MapPolicyRevisionConflict(error),
                    BackupPolicyIdentityImmutable error => MapPolicyIdentityImmutable(error),
                    BackupPolicyStopping error => MapPolicyStopping(error),
                    BackupPolicyGitOwned error => MapPolicyGitOwned(error),
                }))
            .WithName("Backups.UpdateBackupPolicy");
        return endpoints;
    }
}
