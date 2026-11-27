// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Policies;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Backups.Application.Common.BackupPolicyHttpMappings;

namespace Pole.Features.Backups.Application.Endpoints.Policies;

public sealed record DeleteBackupPolicyRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id,
    [FromQuery] long ExpectedRevision
) : IMediatorRequest<DeleteBackupPolicyOutcome>, ITenantWriteOperation;

public union DeleteBackupPolicyOutcome(
    BackupPolicyDeletionRequested,
    BackupPolicyMissing,
    BackupPolicyRevisionConflict,
    BackupPolicyGitOwned);

public sealed class DeleteBackupPolicyValidator : AbstractValidator<DeleteBackupPolicyRequest>
{
    public DeleteBackupPolicyValidator()
    {
        RuleFor(r => r.Id).MongoObjectId("Resource ID");
        RuleFor(r => r.ExpectedRevision).GreaterThan(0);
    }
}

public sealed class DeleteBackupPolicyHandler(
    BeginBackupPolicyDeletion deletion
) : IMediatorOutcomeHandler<DeleteBackupPolicyRequest, DeleteBackupPolicyOutcome>
{
    public async Task<DeleteBackupPolicyOutcome> HandleAsync(
        DeleteBackupPolicyRequest request,
        CancellationToken ct = default)
    {
        var answer = await deletion.ExecuteAsync(new(request.TenantId, request.Id, request.ExpectedRevision), ct);
        return answer switch
        {
            BackupPolicyDeletionRequested value => value,
            BackupPolicyMissing error => error,
            BackupPolicyRevisionConflict error => error,
            BackupPolicyGitOwned error => error,
        };
    }
}

public static class DeleteBackupPolicyEndpoint
{
    public static IEndpointRouteBuilder MapDeleteBackupPolicy(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete(
                "/policies/{Id}",
                async (
                    IMediator mediator, [AsParameters] DeleteBackupPolicyRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupPolicyDeletionRequested => Results.NoContent(),
                    BackupPolicyMissing error => MapPolicyMissing(error),
                    BackupPolicyRevisionConflict error => MapPolicyRevisionConflict(error),
                    BackupPolicyGitOwned error => MapPolicyGitOwned(error),
                }))
            .WithName("Backups.DeleteBackupPolicy");
        return endpoints;
    }
}
