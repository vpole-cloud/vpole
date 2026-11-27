// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Contracts;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Backups.Application.Common.BackupArchiveHttpMappings;
using static Pole.Features.Backups.Application.Common.BackupPolicyHttpMappings;

namespace Pole.Features.Backups.Application.Endpoints.Archives;

public sealed record ListBackupCatalogRequest(
    [FromTenantHeader] string TenantId,
    [FromRoute] string Id
) : IMediatorRequest<ListBackupCatalogOutcome>, ITenantReadOperation;

public union ListBackupCatalogOutcome(
    BackupCatalogEntry[],
    BackupArchiveMissing,
    BackupSourceUnsupported,
    BackupCatalogCredentialUnavailable,
    BackupCatalogAccessDenied,
    BackupCatalogUnavailable,
    BackupCatalogUnreadable,
    BackupCatalogTimeout);

public sealed class ListBackupCatalogValidator : AbstractValidator<ListBackupCatalogRequest>
{
    public ListBackupCatalogValidator()
    {
        RuleFor(r => r.Id).MongoObjectId("Archive ID");
    }
}

public sealed class ListBackupCatalogHandler(
    LoadArchiveBackupCatalog catalog
) : IMediatorOutcomeHandler<ListBackupCatalogRequest, ListBackupCatalogOutcome>
{
    public async Task<ListBackupCatalogOutcome> HandleAsync(
        ListBackupCatalogRequest request,
        CancellationToken ct = default)
    {
        var answer = await catalog.ExecuteAsync(new(request.TenantId, request.Id), ct);
        return answer switch
        {
            BackupCatalogContents contents => contents.Entries.ToArray(),
            BackupArchiveMissing error => error,
            BackupSourceUnsupported error => error,
            BackupCatalogCredentialUnavailable error => error,
            BackupCatalogAccessDenied error => error,
            BackupCatalogUnavailable error => error,
            BackupCatalogUnreadable error => error,
            BackupCatalogTimeout error => error,
        };
    }
}

public static class ListBackupCatalogEndpoint
{
    public static IEndpointRouteBuilder MapListBackupCatalog(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/archives/{Id}/backups",
                async (
                    IMediator mediator, [AsParameters] ListBackupCatalogRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupCatalogEntry[] value => Results.Ok(value),
                    BackupArchiveMissing error => MapArchiveMissing(error),
                    BackupSourceUnsupported error => MapSourceUnsupported(error),
                    BackupCatalogCredentialUnavailable error => MapCatalogCredentialUnavailable(error),
                    BackupCatalogAccessDenied error => MapCatalogAccessDenied(error),
                    BackupCatalogUnavailable error => MapCatalogUnavailable(error),
                    BackupCatalogUnreadable error => MapCatalogUnreadable(error),
                    BackupCatalogTimeout error => MapCatalogTimeout(error),
                }))
            .WithName("Backups.ListBackupCatalog");
        return endpoints;
    }
}
