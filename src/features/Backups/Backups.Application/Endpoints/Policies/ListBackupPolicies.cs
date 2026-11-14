using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Policies;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Backups.Application.Endpoints.Policies;

public sealed record ListBackupPoliciesRequest(
    [FromTenantHeader] string TenantId
) : IMediatorRequest<ListBackupPoliciesOutcome>, ITenantReadOperation;

public union ListBackupPoliciesOutcome(BackupPolicyDto[]);

public sealed class ListBackupPoliciesHandler(
    BackupPolicyQueries reads
) : IMediatorOutcomeHandler<ListBackupPoliciesRequest, ListBackupPoliciesOutcome>
{
    public async Task<ListBackupPoliciesOutcome> HandleAsync(
        ListBackupPoliciesRequest request,
        CancellationToken ct = default)
    {
        return await reads.PoliciesAsync(request.TenantId, ct);
    }
}

public static class ListBackupPoliciesEndpoint
{
    public static IEndpointRouteBuilder MapListBackupPolicies(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/policies",
                async (
                    IMediator mediator, [AsParameters] ListBackupPoliciesRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    BackupPolicyDto[] value => Results.Ok(value)
                }))
            .WithName("Backups.ListBackupPolicies");
        return endpoints;
    }
}
