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
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;

namespace Pole.Features.Backups.Application.Endpoints.Operations;

public sealed record ListBackupOperationsRequest(
    [FromTenantHeader] string TenantId,
    [FromQuery] string? After,
    [FromQuery] int? PageSize
) : IMediatorRequest<ListBackupOperationsOutcome>, ITenantReadOperation;

public sealed record BackupOperationPage(
    BackupOperationDto[] Items,
    string? NextCursor
);

public union ListBackupOperationsOutcome(BackupOperationPage);

public sealed class ListBackupOperationsValidator : AbstractValidator<ListBackupOperationsRequest>
{
    public ListBackupOperationsValidator()
    {
        RuleFor(request => request.PageSize).Must(size => size is null or >= 1 and <= 200);
        RuleFor(request => request.After).Must(value => value is null || BackupOperationCursor.TryParse(value, out _));
    }
}

public sealed class ListBackupOperationsHandler(
    LoadBackupOperationHistory history
) : IMediatorOutcomeHandler<ListBackupOperationsRequest, ListBackupOperationsOutcome>
{
    public async Task<ListBackupOperationsOutcome> HandleAsync(
        ListBackupOperationsRequest request,
        CancellationToken ct = default)
    {
        BackupOperationCursor? after = null;
        if (request.After is { } encoded)
        {
            if (!BackupOperationCursor.TryParse(encoded, out var parsed))
                throw new InvalidOperationException("An invalid history cursor passed request validation.");
            after = parsed;
        }

        var page = await history.LoadAsync(new(request.TenantId, after, request.PageSize ?? 50), ct);
        return new BackupOperationPage(page.Items.Select(BackupViews.From).ToArray(), page.NextCursor);
    }
}

public static class ListBackupOperationsEndpoint
{
    public static IEndpointRouteBuilder MapListBackupOperations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/operations",
                async (
                    IMediator mediator, [AsParameters] ListBackupOperationsRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupOperationPage page => Results.Ok(page),
                }))
            .WithName("Backups.ListBackupOperations");
        return endpoints;
    }
}
