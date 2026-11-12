using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Primitives;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;
using static Pole.Features.Secrets.Application.SecretHttpMappings;

namespace Pole.Features.Secrets.Application.Endpoints;

public union GetSecretUsageOutcome(SecretUsage[], SecretMissing);

public sealed record GetSecretUsageRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetSecretUsageOutcome>, ITenantReadOperation;

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
    IGitOpsMutations git
) : IMediatorOutcomeHandler<GetSecretUsageRequest, GetSecretUsageOutcome>
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
                new LoadResourceDeploymentSetRequest(
                    TenantId: secret.TenantId,
                    ResourceType: ResourceTypeId.Create(binding.Consumer.ResourceType),
                    ResourceId: binding.Consumer.ResourceId),
                cancellationToken: cancellationToken);
            usages.Add(
                new SecretUsage(
                    SecretBindingDto.From(binding) with
                    {
                        GitOps = await git.StatusAsync(
                            new LoadGitResourceStatusRequest(
                                TenantId: request.TenantId,
                                Kind: "SecretBinding",
                                ResourceId: binding.Id),
                            ct: cancellationToken),
                    },
                    deployments.Where(x =>
                            binding.GrantsRuntimeProjection() && x.IsolationScopeId == secret.IsolationScopeId &&
                            x.DesiredOperation != ResourceDeploymentDesiredOperation.Delete &&
                            (binding.Selection is AllSecretDeployments ||
                                (binding.Selection is SelectedSecretDeployments selected &&
                                    selected.DeploymentIds.Contains(x.DeploymentId))))
                        .Select(x => x.DeploymentId)
                        .ToArray()));
        }

        return usages.ToArray();
    }
}

public static class GetSecretUsageEndpoint
{
    public static IEndpointRouteBuilder MapGetSecretUsageEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}/usage",
                async (
                    IMediator mediator, [AsParameters] GetSecretUsageRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SecretUsage[] usage => Results.Ok(usage),
                    SecretMissing error => MapMissing(error),
                }))
            .WithName("GetSecretUsage");

        return app;
    }
}

public sealed record SecretUsage(
    SecretBindingDto Binding,
    string[] AffectedDeploymentIds
);

public sealed record SecretBindingDto(
    string Id,
    string ConsumerType,
    string ConsumerId,
    string Usage,
    bool AllDeployments,
    string[] DeploymentIds,
    ImmutableDictionary<string, string> KeyMappings,
    GitResourceStatus? GitOps = null
)
{
    public static SecretBindingDto From(SecretBinding binding) => new(
        binding.Id,
        binding.Consumer.ResourceType,
        binding.Consumer.ResourceId,
        binding.Usage.ToString(),
        binding.Selection is AllSecretDeployments,
        binding.Selection is SelectedSecretDeployments selected ? selected.DeploymentIds.ToArray() : [],
        binding.KeyMappings);
}
