// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
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

public union CreateBackupPolicyOutcome(
    BackupPolicyDto,
    BackupSourceUnavailable,
    BackupSourceUnsupported,
    BackupSourcePlacementUnavailable,
    BackupSourceRevisionChanged,
    BackupDestinationPlacementUnavailable,
    BackupRetentionInvalid,
    BackupPolicyDestinationUnavailable,
    BackupPolicyNameAlreadyExists,
    BackupPolicySourceAlreadyBound);

public sealed class CreateBackupPolicyValidator : AbstractValidator<CreateBackupPolicyRequest>
{
    public CreateBackupPolicyValidator()
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
    }
}

public sealed class CreateBackupPolicyHandler(
    BackupPolicyWrites writes,
    TimeProvider clock
) : IMediatorOutcomeHandler<CreateBackupPolicyRequest, CreateBackupPolicyOutcome>
{
    public async Task<CreateBackupPolicyOutcome> HandleAsync(
        CreateBackupPolicyRequest request,
        CancellationToken ct = default)
    {
        var input = request.Body.ToInput();
        var answer = await writes.CreateAsync(new(request.TenantId, ObjectId.GenerateNewId().ToString(), input), ct);
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
            BackupPolicyNameAlreadyExists error => error,
            BackupPolicySourceAlreadyBound error => error,
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
                    BackupSourceUnavailable error => MapSourceUnavailable(error),
                    BackupSourceUnsupported error => MapSourceUnsupported(error),
                    BackupSourcePlacementUnavailable error => MapSourcePlacementUnavailable(error),
                    BackupSourceRevisionChanged error => MapSourceRevisionChanged(error),
                    BackupDestinationPlacementUnavailable error => MapDestinationPlacementUnavailable(error),
                    BackupRetentionInvalid error => MapRetentionInvalid(error),
                    BackupPolicyDestinationUnavailable error => MapDestinationUnavailable(error),
                    BackupPolicyNameAlreadyExists error => MapPolicyNameExists(error),
                    BackupPolicySourceAlreadyBound error => MapPolicySourceBound(error),
                }))
            .WithName("Backups.CreateBackupPolicy");
        return endpoints;
    }
}
