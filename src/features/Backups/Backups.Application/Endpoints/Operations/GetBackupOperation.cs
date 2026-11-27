// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Backups.Application.Common.BackupOperationHttpMappings;

namespace Pole.Features.Backups.Application.Endpoints.Operations;

public sealed record GetBackupOperationRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id
) : IMediatorRequest<GetBackupOperationOutcome>, ITenantReadOperation;

public union GetBackupOperationOutcome(BackupOperationDto, BackupOperationMissing);

public sealed class GetBackupOperationValidator : AbstractValidator<GetBackupOperationRequest>
{
    public GetBackupOperationValidator() => RuleFor(r => r.Id).MongoObjectId("Resource ID");
}

public sealed class GetBackupOperationHandler(
    LoadBackupOperation load
) : IMediatorOutcomeHandler<GetBackupOperationRequest, GetBackupOperationOutcome>
{
    public async Task<GetBackupOperationOutcome> HandleAsync(
        GetBackupOperationRequest request,
        CancellationToken ct = default)
    {
        var result = await load.ExecuteAsync(new(request.TenantId, request.Id), ct);
        return result switch
        {
            BackupOperation operation => BackupViews.From(operation),
            BackupOperationMissing error => error,
        };
    }
}

public static class GetBackupOperationEndpoint
{
    public static IEndpointRouteBuilder MapGetBackupOperation(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/operations/{Id}",
                async (
                    IMediator mediator, [AsParameters] GetBackupOperationRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupOperationDto item => Results.Ok(item),
                    BackupOperationMissing error => MapOperationMissing(error)
                }))
            .WithName("Backups.GetBackupOperation");
        return endpoints;
    }
}
