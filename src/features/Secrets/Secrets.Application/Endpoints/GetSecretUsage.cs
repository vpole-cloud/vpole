using Pole.Features.GitOps.Contracts;
using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Domain;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;

using static Pole.Features.Secrets.Application.SecretHttpMappings;

namespace Pole.Features.Secrets.Application.Endpoints;

public union GetSecretUsageOutcome(
    SecretUsage[],
    SecretMissing);

public sealed record GetSecretUsageRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetSecretUsageOutcome>, ITenantReadOperation;

public sealed class GetSecretUsageValidator : AbstractValidator<GetSecretUsageRequest>
{
    public GetSecretUsageValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Secret ID");
    }
}

public sealed class GetSecretUsageHandler(
    LoadSecretById load,
    LoadResourceDeploymentSet loadDeployments,
    IGitOpsMutations git)
    : IMediatorOutcomeHandler<GetSecretUsageRequest, GetSecretUsageOutcome>
{
    public async Task<GetSecretUsageOutcome> HandleAsync(
        GetSecretUsageRequest request,
        CancellationToken cancellationToken = default)
    {
        var secret = await load(request.TenantId, request.Id, cancellationToken);
        if (secret is null)
            return new SecretMissing();

        var usages = new List<SecretUsage>();
        foreach (var binding in secret.Bindings)
        {
            var deployments = await loadDeployments(
                secret.TenantId,
                ResourceTypeId.Create(binding.ConsumerType),
                binding.ConsumerId,
                cancellationToken);
            usages.Add(new(
                SecretBindingDto.From(binding) with { GitOps = await git.StatusAsync(request.TenantId, "SecretBinding", binding.Id, cancellationToken) },
                deployments.Where(x => x.IsolationScopeId == secret.IsolationScopeId &&
                    x.DesiredOperation != ResourceDeploymentDesiredOperation.Delete &&
                    (binding.Selection is AllSecretDeployments ||
                     binding.Selection is SelectedSecretDeployments selected && selected.DeploymentIds.Contains(x.DeploymentId)))
                    .Select(x => x.DeploymentId).ToArray()));
        }
        return usages.ToArray();
    }
}

public static class GetSecretUsageEndpoint
{
    public static IEndpointRouteBuilder MapGetSecretUsageEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/usage", async (
                IMediator mediator,
                [AsParameters] GetSecretUsageRequest request,
                CancellationToken ct) =>
                (await mediator.Send(request, ct))
                .ToHttpResult(outcome => outcome switch
                {
                    SecretUsage[] usage => Results.Ok(usage),
                    SecretMissing error => MapMissing(error)
                }))
            .WithName("GetSecretUsage");

        return app;
    }
}

public sealed record SecretUsage(
    SecretBindingDto Binding,
    string[] AffectedDeploymentIds);

public sealed record SecretBindingDto(
    string Id,
    string ConsumerType,
    string ConsumerId,
    bool AllDeployments,
    string[] DeploymentIds,
    ImmutableDictionary<string, string> KeyMappings,
    GitResourceStatus? GitOps = null)
{
    public static SecretBindingDto From(SecretBinding binding) => new(
        binding.Id,
        binding.ConsumerType,
        binding.ConsumerId,
        binding.Selection is AllSecretDeployments,
        binding.Selection is SelectedSecretDeployments selected ? selected.DeploymentIds.ToArray() : [],
        binding.KeyMappings);
}
