using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Destinations;
using Pole.Features.Backups.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Secrets.Contracts;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Backups.Application.Common.BackupDestinationHttpMappings;

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

public union UpdateBackupDestinationOutcome(
    BackupDestinationDto,
    BackupDestinationMissing,
    BackupDestinationRevisionConflict,
    BackupDestinationIdentityImmutable,
    BackupDestinationGitOwned);

public sealed class UpdateBackupDestinationValidator : AbstractValidator<UpdateBackupDestinationRequest>
{
    public UpdateBackupDestinationValidator()
    {
        RuleFor(r => r.Body).NotNull();
        When(
            r => r.Body is not null,
            () =>
            {
                RuleFor(r => r.Body.Name)
                    .Must(BackupInputRules.ValidName)
                    .WithMessage("Use a lowercase resource name of at most 63 characters.");
                RuleFor(r => r.Body.ResourceGroupId).MongoObjectId("Resource group ID");
                RuleFor(r => r.Body.IsolationScopeId).MongoObjectId("Isolation scope ID");
                RuleFor(r => r.Body.Endpoint)
                    .Must(BackupInputRules.ValidEndpoint)
                    .WithMessage("Use an HTTP(S) origin without credentials, path, query or fragment.");
                RuleFor(r => r.Body.Bucket)
                    .Must(BackupInputRules.ValidBucket)
                    .WithMessage("Use a valid bucket name of 3 to 63 characters.");
                RuleFor(r => r.Body.Prefix)
                    .Must(BackupInputRules.ValidPrefix)
                    .WithMessage(
                        "Use a prefix of at most 512 characters without control characters or relative path segments.");
                RuleFor(r => r.Body.CredentialSecretId).MongoObjectId("Credential Secret ID");
                RuleFor(r => r.Body.AccessKeyIdKey)
                    .Must(SecretKeyReferenceRules.ValidKey)
                    .WithMessage("Use a valid Secret key name.");
                RuleFor(r => r.Body.SecretAccessKeyKey)
                    .Must(SecretKeyReferenceRules.ValidKey)
                    .WithMessage("Use a valid Secret key name.");
                RuleFor(r => r.Body.SecretAccessKeyKey)
                    .NotEqual(r => r.Body.AccessKeyIdKey)
                    .WithMessage("Select two distinct credential keys.");
            });
        RuleFor(r => r.Id).MongoObjectId("Resource ID");
        When(r => r.Body is not null, () => RuleFor(r => r.Body.ExpectedRevision).GreaterThan(0));
    }
}

public sealed class UpdateBackupDestinationHandler(
    BackupDestinationWrites writes
) : IMediatorOutcomeHandler<UpdateBackupDestinationRequest, UpdateBackupDestinationOutcome>
{
    public async Task<UpdateBackupDestinationOutcome> HandleAsync(
        UpdateBackupDestinationRequest request,
        CancellationToken ct = default)
    {
        var input = request.Body.ToInput();
        var answer = await writes.UpdateAsync(
            new(request.TenantId, request.Id, request.Body.ExpectedRevision, input),
            ct);
        return answer switch
        {
            BackupDestination destination => BackupDestinationDto.From(destination),
            BackupDestinationMissing error => error,
            BackupDestinationRevisionConflict error => error,
            BackupDestinationIdentityImmutable error => error,
            BackupDestinationGitOwned error => error,
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
                    BackupDestinationMissing error => MapDestinationMissing(error),
                    BackupDestinationRevisionConflict error => MapDestinationRevisionConflict(error),
                    BackupDestinationIdentityImmutable error => MapDestinationIdentityImmutable(error),
                    BackupDestinationGitOwned error => MapDestinationGitOwned(error),
                }))
            .WithName("Backups.UpdateBackupDestination");
        return endpoints;
    }
}
