using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
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

public sealed record CreateBackupDestinationBody(
    string Name,
    string ResourceGroupId,
    string IsolationScopeId,
    string Endpoint,
    string Bucket,
    string Prefix,
    string CredentialSecretId,
    string AccessKeyIdKey,
    string SecretAccessKeyKey
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

public sealed record CreateBackupDestinationRequest(
    [FromTenantHeader] string TenantId,
    [FromBody] CreateBackupDestinationBody Body
) : IMediatorRequest<CreateBackupDestinationOutcome>, ITenantCreateOperation;

public union CreateBackupDestinationOutcome(
    BackupDestinationDto,
    BackupDestinationNameAlreadyExists,
    BackupDestinationScopeUnavailable,
    BackupDestinationRegistrationConflict,
    BackupDestinationCredentialGrantDenied);

public sealed class CreateBackupDestinationValidator : AbstractValidator<CreateBackupDestinationRequest>
{
    public CreateBackupDestinationValidator()
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
    }
}

public sealed class CreateBackupDestinationHandler(
    BackupDestinationWrites writes
) : IMediatorOutcomeHandler<CreateBackupDestinationRequest, CreateBackupDestinationOutcome>
{
    public async Task<CreateBackupDestinationOutcome> HandleAsync(
        CreateBackupDestinationRequest request,
        CancellationToken ct = default)
    {
        var input = request.Body.ToInput();
        var answer = await writes.CreateAsync(new(request.TenantId, ObjectId.GenerateNewId().ToString(), input), ct);
        return answer switch
        {
            BackupDestination destination => BackupDestinationDto.From(destination),
            BackupDestinationNameAlreadyExists error => error,
            BackupDestinationScopeUnavailable error => error,
            BackupDestinationRegistrationConflict error => error,
            BackupDestinationCredentialGrantDenied error => error,
        };
    }
}

public static class CreateBackupDestinationEndpoint
{
    public static IEndpointRouteBuilder MapCreateBackupDestination(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/destinations",
                async (
                    IMediator mediator, [AsParameters] CreateBackupDestinationRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupDestinationDto value => Results.Created($"/api/v1/backups/destinations/{value.Id}", value),
                    BackupDestinationNameAlreadyExists error => MapDestinationNameExists(error),
                    BackupDestinationScopeUnavailable error => MapDestinationScopeUnavailable(error),
                    BackupDestinationRegistrationConflict error => MapDestinationRegistrationConflict(error),
                    BackupDestinationCredentialGrantDenied error => MapDestinationCredentialGrantDenied(error),
                }))
            .WithName("Backups.CreateBackupDestination");
        return endpoints;
    }
}
