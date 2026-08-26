using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Features.ResourceGroups.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class CreateResourceGroup
{
    public static IEndpointRouteBuilder MapCreateResourceGroupEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (
                [AsParameters] CreateResourceGroupRequest request,
                [FromServices] IMediator mediator,
                IHttpProblemMapper httpProblems,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    ResourceGroupDto resourceGroup =>
                        Results.Created($"/resource-groups/{resourceGroup.Id}", resourceGroup),
                    CreateResourceGroupError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("CreateResourceGroup")
            .WithSummary("Creates a new resource group.")
            .WithDescription("Creates a new resource group with the specified details.")
            .Produces<ResourceGroupDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
    
}

public sealed record CreateResourceGroupRequest(
    [FromBody] CreateResourceGroupRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<CreateResourceGroupOutcome>, ITenantCreateOperation;

public union CreateResourceGroupError(DefaultDeploymentTargetWorkloadAdmissionError);

public union CreateResourceGroupOutcome(ResourceGroupDto, CreateResourceGroupError);

public sealed record CreateResourceGroupRequestBody(
    string Name,
    string Description,
    ImmutableHashSet<string> Tags
    );

public sealed class CreateResourceGroupHandler(
    GenerateResourceGroupNamespaceName generateNamespaceName,
    AdmitDefaultDeploymentTargetForNewWorkload admitDefaultDeploymentTarget,
    PersistCreatedResourceGroup persistCreatedResourceGroup)
    : IMediatorOutcomeHandler<CreateResourceGroupRequest, CreateResourceGroupOutcome>
{
    public async Task<CreateResourceGroupOutcome> HandleAsync(
        CreateResourceGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var admission = await admitDefaultDeploymentTarget(cancellationToken);
        if (admission is Err<DefaultDeploymentTargetWorkloadAdmissionError> error)
            return new CreateResourceGroupError(error.Value);

        var createOptions = ResourceGroupFactory.DefaultCreateOptions with
        {
            Tags = request.Body.Tags
        };
        var resourceGroup = ResourceGroupFactory.Create(
            tenantId: request.TenantId,
            name: request.Body.Name,
            description: request.Body.Description,
            kubernetesNamespace: generateNamespaceName(request.TenantId, createOptions.Id),
            options: createOptions
        );
        var defaultScope = IsolationScopeFactory.CreateDefault(
            request.TenantId,
            resourceGroup.Id,
            new IsolationScopeFactory.CreateOptions(
                resourceGroup.DefaultIsolationScopeId,
                resourceGroup.CreatedAtUtc));
        var persisted = await persistCreatedResourceGroup(
            resourceGroup,
            defaultScope,
            cancellationToken);
        return persisted.ToDto();
    }
}

public sealed class CreateResourceGroupValidator : AbstractValidator<CreateResourceGroupRequest>
{
    public CreateResourceGroupValidator()
    {
        RuleFor(x => x.Body.Name)
            .NotEmpty()
            .MaximumLength(20);
        RuleFor(x => x.Body.Description)
            .MaximumLength(200);
        RuleForEach(x => x.Body.Tags)
            .NotEmpty()
            .MaximumLength(30);
    }
}
