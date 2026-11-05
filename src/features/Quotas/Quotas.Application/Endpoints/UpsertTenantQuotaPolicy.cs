using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Mongo;
using Pole.Features.Quotas.Application.Policies;
using Pole.Mediator;

namespace Pole.Features.Quotas.Application.Endpoints;

public static class UpsertTenantQuotaPolicy
{
    public static IEndpointRouteBuilder MapUpsertTenantQuotaPolicyEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{tenantId}/quota", async (
                IMediator mediator,
                [AsParameters] UpsertTenantQuotaPolicyRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    TenantQuotaPolicyDto policy => Results.Ok(policy)
                });
            })
            .WithName("UpsertTenantQuotaPolicy")
            .RequireAuthorization(Policy.Admin)
            .Produces<TenantQuotaPolicyDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }
}

public sealed record UpsertTenantQuotaPolicyRequest(
    [FromRoute] string TenantId,
    [FromBody] UpsertTenantQuotaPolicyRequestBody Body)
    : IMediatorRequest<UpsertTenantQuotaPolicyOutcome>, IPlatformTenantOperation;

public union UpsertTenantQuotaPolicyOutcome(TenantQuotaPolicyDto);

public sealed class UpsertTenantQuotaPolicyHandler(
    PersistTenantQuotaPolicy persistTenantQuotaPolicy,
    QuotaCatalog catalog,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<
        UpsertTenantQuotaPolicyRequest,
        UpsertTenantQuotaPolicyOutcome>
{
    public async Task<UpsertTenantQuotaPolicyOutcome> HandleAsync(
        UpsertTenantQuotaPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        var policy = await persistTenantQuotaPolicy(
            request.TenantId,
            request.Body.ToQuotaLimits(catalog),
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        return policy.ToDto(policy.TenantId, catalog);
    }
}

public sealed class UpsertTenantQuotaPolicyValidator
    : AbstractValidator<UpsertTenantQuotaPolicyRequest>
{
    public UpsertTenantQuotaPolicyValidator(QuotaCatalog catalog)
    {
        RuleFor(request => request.Body)
            .NotNull();
        RuleFor(request => request.Body)
            .Custom((body, context) =>
            {
                foreach (var (field, messages) in QuotaDtoExtensions.ValidateQuotaLimits(body, catalog))
                {
                    foreach (var message in messages)
                        context.AddFailure(field, message);
                }
            });
    }
}
