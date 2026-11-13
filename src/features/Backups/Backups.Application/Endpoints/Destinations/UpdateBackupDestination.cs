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

namespace Pole.Features.Backups.Application.Endpoints.Destinations;

public sealed record UpdateBackupDestinationBody(
    string Name,
    string ResourceGroupId,
    string IsolationScopeId,
    string Endpoint,
    string Bucket,
    string Prefix,
    string CredentialSecretId,
    string AccessKeyIdKey,
    string SecretAccessKeyKey,
    long ExpectedRevision
)
{
    public BackupDestinationInput ToInput() => new(
        Name,
        ResourceGroupId,
        IsolationScopeId,
        Endpoint,
        Bucket,
        Prefix,
        CredentialSecretId,
        AccessKeyIdKey,
        SecretAccessKeyKey);
}

public sealed record UpdateBackupDestinationRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id,
    [FromBody] UpdateBackupDestinationBody Body
) : IMediatorRequest<UpdateBackupDestinationOutcome>, ITenantWriteOperation;

public union UpdateBackupDestinationOutcome(BackupDestinationDto, BackupRequestRejected);

public sealed class UpdateBackupDestinationValidator : AbstractValidator<UpdateBackupDestinationRequest>
{
    public UpdateBackupDestinationValidator()
    {
        RuleFor(r => r.Body).NotNull();
        When(r => r.Body is not null, () => RuleFor(r => r.Body).Must(b => BackupInputRules.Valid(b.ToInput())));
        RuleFor(r => r.Id).MongoObjectId("Resource ID");
        When(r => r.Body is not null, () => RuleFor(r => r.Body.ExpectedRevision).GreaterThan(0));
    }
}

public sealed class UpdateBackupDestinationHandler(
    ManualBackupConfiguration configuration
) : IMediatorOutcomeHandler<UpdateBackupDestinationRequest, UpdateBackupDestinationOutcome>
{
    public async Task<UpdateBackupDestinationOutcome> HandleAsync(
        UpdateBackupDestinationRequest request,
        CancellationToken ct = default)
    {
        var input = request.Body.ToInput();
        var answer = await configuration.SaveDestinationAsync(
            new(request.TenantId, request.Id, request.Body.ExpectedRevision, input),
            ct);
        return answer switch
        {
            BackupWriteResult<BackupDestination>.Saved ok => BackupDestinationDto.From(ok.Value),
            BackupWriteResult<BackupDestination>.Rejected failure => new BackupRequestRejected(failure.Code)
        };
    }
}

public static class UpdateBackupDestinationEndpoint
{
    public static IEndpointRouteBuilder MapUpdateBackupDestination(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut(
                "/destinations/{Id}",
                async (
                    IMediator mediator, [AsParameters] UpdateBackupDestinationRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupDestinationDto value => Results.Ok(value),
                    BackupRequestRejected error => BackupHttpProblems.From(error)
                }))
            .WithName("Backups.UpdateBackupDestination");
        return endpoints;
    }
}
