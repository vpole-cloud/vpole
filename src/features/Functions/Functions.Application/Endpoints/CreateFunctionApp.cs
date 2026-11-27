// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Contracts.HttpMapping;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Contracts.HttpMapping;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Contracts.HttpMapping;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Contracts.HttpMapping;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Contracts.HttpMapping;
using Pole.Features.Secrets.Contracts;
using Pole.Functions.Protocol;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;
using static Pole.Features.Functions.Application.Common.FunctionAppHttpMappings;

namespace Pole.Features.Functions.Application.Endpoints;

public static class CreateFunctionApp
{
    public static IEndpointRouteBuilder MapCreateFunctionAppEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/",
                async (
                    IMediator mediator, [AsParameters] CreateFunctionAppRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        CreateFunctionAppResponse response => Results.Accepted(value: response),
                        CreateFunctionAppError error => MapCreateError(error),
                    });
                })
            .WithName("CreateFunctionApp")
            .Produces<CreateFunctionAppResponse>(StatusCodes.Status202Accepted);
        return app;
    }

    private static HttpProblem MapCreateError(CreateFunctionAppError error) => error switch
    {
        FunctionSecretReferenceRejected rejected => MapSecretReferenceRejected(rejected),
        FunctionAppDefaultDeploymentTargetUnavailable unavailable => MapDefaultDeploymentTargetUnavailable(unavailable),
        DeploymentTargetNewWorkloadAdmissionError admission => DeploymentTargetContractHttpMappings
            .MapNewWorkloadAdmissionError(admission),
        ComputeProfileResolutionError resolution => ComputeProfileContractHttpMappings.MapResolutionError(resolution),
        CreateResourceDeploymentError deployment => ResourceDeploymentContractHttpMappings.MapDeploymentError(
            deployment),
        ResourceRegistrationMutationError registration =>
            ResourceGroupContractHttpMappings.MapRegistrationMutationError(registration),
        QuotaExecutionError quota => QuotaContractHttpMappings.MapExecutionError(quota),
    };
}

public sealed record CreateFunctionAppRequest(
    [FromBody] CreateFunctionAppRequestBody Body,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<CreateFunctionAppOutcome>, ITenantCreateOperation;

public sealed record CreateFunctionAppRequestBody(
    string Name,
    string FunctionRoute,
    string ResourceGroupId,
    FunctionRevisionRequest Spec,
    ImmutableHashSet<string> Tags,
    string? ComputeProfileId = null,
    FunctionPlacementRequest? Placement = null
);

public sealed record FunctionPlacementRequest(
    string DeploymentTargetId,
    string? IsolationScopeId
);

public sealed record FunctionRevisionRequest(
    FunctionImageRequest Image,
    FunctionConfiguration Configuration,
    FunctionResources Resources,
    FunctionScalingPolicy Scaling,
    ImmutableList<FunctionHandlerRequest> Handlers
)
{
    public FunctionRevisionSpecInput ToDomain() => new(
        new FunctionImage(Image.Server, Image.Name, Image.Tag, Digest: null, PullSecretName: null),
        PoleFunctionProtocol.Version,
        Configuration,
        Resources,
        Scaling,
        Handlers.Select(handler => new FunctionHandlerSpec(
                handler.Name,
                handler.MaxConcurrencyPerInstance,
                handler.Trigger switch
                {
                    HttpFunctionTriggerRequest http => new HttpFunctionTriggerSpec(
                        TriggerId: "",
                        http.Route,
                        http.Methods
                            .Select(method => method.ToUpperInvariant())
                            .ToImmutableHashSet(StringComparer.Ordinal),
                        http.Exposure),
                    TimerFunctionTriggerRequest timer => new TimerFunctionTriggerSpec(
                        TriggerId: "",
                        timer.Cron,
                        "UTC",
                        timer.ConcurrencyPolicy,
                        timer.MisfirePolicy,
                        ScheduleVersion: 1,
                        timer.Retry),
                }))
            .ToImmutableList());
}

public sealed record FunctionImageRequest(
    string Server,
    string Name,
    string Tag
);

public sealed record FunctionHandlerRequest(
    string Name,
    int MaxConcurrencyPerInstance,
    FunctionTriggerRequest Trigger
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HttpFunctionTriggerRequest), "http")]
[JsonDerivedType(typeof(TimerFunctionTriggerRequest), "timer")]
public closed record FunctionTriggerRequest;

public sealed record HttpFunctionTriggerRequest(
    string Route,
    ImmutableHashSet<string> Methods,
    FunctionExposure Exposure
) : FunctionTriggerRequest;

public sealed record TimerFunctionTriggerRequest(
    string Cron,
    TimerConcurrencyPolicy ConcurrencyPolicy,
    TimerMisfirePolicy MisfirePolicy,
    FunctionRetryPolicy Retry
) : FunctionTriggerRequest;

public sealed record CreateFunctionAppResponse(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber,
    PlacementSummaryDto Placement
);

public union CreateFunctionAppError(
    FunctionAppDefaultDeploymentTargetUnavailable,
    FunctionSecretReferenceRejected,
    DeploymentTargetNewWorkloadAdmissionError,
    ComputeProfileResolutionError,
    CreateResourceDeploymentError,
    ResourceRegistrationMutationError,
    QuotaExecutionError);

public union CreateFunctionAppOutcome(CreateFunctionAppResponse, CreateFunctionAppError);

public sealed class CreateFunctionAppHandler(
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider,
    LookupDefaultDeploymentTarget lookupDefaultDeploymentTarget,
    AdmitDeploymentTargetForNewWorkload admitDeploymentTarget,
    ResolveComputeProfile resolveComputeProfile,
    CreateResourceDeployment createResourceDeployment,
    IResourceRegistrar resourceRegistrar,
    PersistCreatedFunctionApp persistCreatedFunctionApp,
    AdmitConsumerSecretReferences admitSecrets
) : IMediatorOutcomeHandler<CreateFunctionAppRequest, CreateFunctionAppOutcome>
{
    public async Task<CreateFunctionAppOutcome> HandleAsync(
        CreateFunctionAppRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetId = request.Body.Placement?.DeploymentTargetId;
        if (targetId is null)
        {
            var defaultTarget = await lookupDefaultDeploymentTarget(cancellationToken);
            if (defaultTarget is DefaultDeploymentTargetLookupError)
                return new CreateFunctionAppError(new FunctionAppDefaultDeploymentTargetUnavailable());

            targetId = ((DeploymentTargetLookupDto)defaultTarget.Value).Id;
        }

        var targetResult = await admitDeploymentTarget(
            new DeploymentTargetAdmissionRequest(
                request.TenantId,
                FunctionResourceDeployment.ResourceType,
                request.Body.Placement?.IsolationScopeId,
                targetId,
                DeploymentTargetRequiredCapabilities.None,
                FunctionResourceDeployment.GatewaySourceName),
            cancellationToken);
        if (targetResult is DeploymentTargetNewWorkloadAdmissionError targetError)
            return new CreateFunctionAppError(targetError);
        var target = (DeploymentTargetLookupDto)targetResult.Value;

        var spec = request.Body.Spec.ToDomain();
        var computeResult = await resolveComputeProfile(
            new ComputeProfileResolutionRequest(
                request.Body.ComputeProfileId,
                target.Id,
                request.TenantId,
                FunctionResourceDeployment.ResourceType),
            cancellationToken);
        if (computeResult is ComputeProfileResolutionError computeError)
            return new CreateFunctionAppError(computeError);

        var compute = (ResolvedComputeProfile)computeResult.Value;
        var createOptions = FunctionAppFactory.DefaultCreateOptions;
        var deploymentId = ObjectId.GenerateNewId().ToString();
        var admittedQuota = FunctionQuotaAllocation.FromRevision(spec.ToRevisionSpec());

        return await quotaAdmissionExecutor.ExecuteOutcomeAsync(
            new ExecuteQuotaAdmissionRequest<FunctionQuotaAllocation, CreateFunctionAppOutcome>(
                TenantId: request.TenantId,
                ResourceKey: FunctionAppQuotaResourceKey.Create(createOptions.FunctionAppId),
                Mutation: new CreateQuotaMutation<FunctionQuotaAllocation>(admittedQuota),
                QuotaError: quotaError => new CreateFunctionAppError(quotaError),
                MutationCallback: async (transaction, ct) =>
                {
                    var registration = await resourceRegistrar.RegisterAsync(
                        new RegisterResourceRequest(
                            TenantId: request.TenantId,
                            ResourceGroupId: request.Body.ResourceGroupId,
                            Resource: new ResourceRegistrationDescriptor(
                                FunctionResourceDeployment.ResourceType,
                                createOptions.FunctionAppId,
                                request.Body.Name)),
                        session: transaction.Session,
                        cancellationToken: ct);
                    if (registration is ResourceRegistrationMutationError registrationError)
                        return transaction.Abort(new CreateFunctionAppError(registrationError));
                    var deploymentResult = await createResourceDeployment(
                        new CreateResourceDeploymentRequest(
                            deploymentId,
                            request.TenantId,
                            FunctionResourceDeployment.ResourceType,
                            createOptions.FunctionAppId,
                            request.Body.ResourceGroupId,
                            IsolationScopeId: request.Body.Placement?.IsolationScopeId,
                            ToResolvedResourceDeploymentTarget(target),
                            compute.ToFunctionResourceDeploymentConfiguration(spec.Resources),
                            Storage: null,
                            ResourcePlacementCapability.SingleTarget),
                        transaction.Session,
                        ct);
                    if (deploymentResult is CreateResourceDeploymentError deploymentError)
                        return transaction.Abort(new CreateFunctionAppError(deploymentError));
                    var placement = (PlacementSummaryDto)deploymentResult.Value;

                    var created = FunctionAppFactory.Create(
                        request.TenantId,
                        request.Body.ResourceGroupId,
                        new FunctionPlacementIdentity(
                            placement.DeploymentId,
                            placement.RuntimeCellId,
                            placement.DeploymentTargetId),
                        request.Body.Name,
                        request.Body.FunctionRoute,
                        spec,
                        request.Body.Tags,
                        timeProvider.GetUtcNow(),
                        createOptions);
                    await persistCreatedFunctionApp(transaction.Session, created, ct);
                    var admitted = await admitSecrets(
                        new AdmitConsumerSecretReferencesRequest(
                            request.TenantId,
                            request.Body.ResourceGroupId,
                            placement.IsolationScopeId,
                            FunctionResourceDeployment.ResourceType,
                            created.FunctionApp.Id,
                            [],
                            FunctionSecretReferences.From(created.Revision.Spec.Configuration),
                            ConsumerSecretAvailabilityPolicy.RequireAvailable,
                            PreviousSecretReferenceRetention.DoNotRetain),
                        transaction.Session,
                        ct);
                    if (admitted is ConsumerSecretReferenceUnavailable unavailable)
                        return transaction.Abort(
                            new CreateFunctionAppError(new FunctionSecretReferenceRejected(unavailable.Reason)));

                    return transaction.Commit(
                        new CreateFunctionAppResponse(
                            created.FunctionApp.Id,
                            created.Revision.Id,
                            created.Revision.RevisionNumber,
                            placement));
                }),
            cancellationToken: cancellationToken);
    }

    private static ResolvedResourceDeploymentTarget
        ToResolvedResourceDeploymentTarget(DeploymentTargetLookupDto target) => new(
        target.Id,
        target.WorkloadConfiguration.Revision,
        target.WorkloadConfiguration.Ingress is { } ingress
            ? ResourceDeploymentIngressConfiguration.Create(
                ingress.IngressClassName,
                ingress.ClusterIssuerName,
                ingress.PublicBaseDomain,
                [])
            : null);
}

public sealed class CreateFunctionAppRequestValidator : AbstractValidator<CreateFunctionAppRequest>
{
    public CreateFunctionAppRequestValidator() => RuleFor(request => request.Body)
        .NotNull()
        .SetValidator(new CreateFunctionAppRequestBodyValidator());
}

public sealed class CreateFunctionAppRequestBodyValidator : AbstractValidator<CreateFunctionAppRequestBody>
{
    private static readonly HashSet<string> ReservedRoutes = new(StringComparer.OrdinalIgnoreCase)
        { "api", "functions", "health", "internal", "_pole" };

    public CreateFunctionAppRequestBodyValidator()
    {
        RuleFor(body => body.Name).NotEmpty().MaximumLength(63).Matches("^[a-z][a-z0-9-]{2,62}$");
        RuleFor(body => body.FunctionRoute)
            .NotEmpty()
            .Matches("^[a-z][a-z0-9-]{2,62}$")
            .Must(route => !ReservedRoutes.Contains(route))
            .WithMessage("Function route is reserved.");
        RuleFor(body => body.ResourceGroupId).NotEmpty().MongoObjectId("Resource group ID");
        RuleFor(body => body.Spec).NotNull().SetValidator(new FunctionRevisionSpecInputValidator());
        RuleFor(body => body.Tags).NotNull();
        When(
            body => body.Placement is not null,
            () =>
            {
                RuleFor(body => body.Placement!.DeploymentTargetId).MongoObjectId("Deployment target ID");
                RuleFor(body => body.Placement!.IsolationScopeId).OptionalMongoObjectId("Isolation scope ID");
            });
        RuleFor(body => body.ComputeProfileId).OptionalMongoObjectId("Compute profile ID");
    }
}

public sealed class FunctionRevisionSpecInputValidator : AbstractValidator<FunctionRevisionRequest>
{
    public FunctionRevisionSpecInputValidator()
    {
        RuleFor(spec => spec.Image).NotNull().SetValidator(new FunctionImageRequestValidator());
        RuleFor(spec => spec.Configuration).NotNull().SetValidator(new FunctionConfigurationValidator());
        RuleFor(spec => spec.Resources).NotNull().SetValidator(new FunctionResourcesValidator());
        RuleFor(spec => spec.Scaling).NotNull().SetValidator(new FunctionScalingPolicyValidator());
        RuleFor(spec => spec.Handlers).NotNull().NotEmpty();
        When(
            spec => spec.Handlers is not null,
            () =>
            {
                RuleFor(spec => spec.Handlers)
                    .Must(handlers =>
                        handlers.Select(handler => handler?.Name).Distinct(StringComparer.Ordinal).Count() ==
                        handlers.Count)
                    .WithMessage("Handler names must be unique.");
                RuleForEach(spec => spec.Handlers).NotNull().SetValidator(new FunctionHandlerSpecValidator());
                RuleFor(spec => spec.Handlers)
                    .Must(HaveNoOverlappingHttpRoutes)
                    .WithMessage("HTTP handler routes and methods must not overlap.");
            });
        When(
            spec => spec.Handlers is not null && spec.Scaling is not null,
            () =>
            {
                RuleFor(spec => spec)
                    .Must(spec => spec.Handlers.All(handler => handler is not null &&
                        handler.MaxConcurrencyPerInstance <= spec.Scaling.MaxConcurrencyPerInstance))
                    .WithMessage("Handler concurrency cannot exceed app concurrency.");
            });
    }

    private static bool HaveNoOverlappingHttpRoutes(ImmutableList<FunctionHandlerRequest> handlers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return handlers.Where(handler => handler?.Trigger is HttpFunctionTriggerRequest)
            .SelectMany(handler =>
            {
                var trigger = (HttpFunctionTriggerRequest)handler.Trigger;
                return trigger.Methods?.Select(method =>
                    $"{FunctionTriggerRules.CanonicalizeRoute(trigger.Route)}:{method}") ?? [];
            })
            .All(seen.Add);
    }
}

public sealed class FunctionImageRequestValidator : AbstractValidator<FunctionImageRequest>
{
    public FunctionImageRequestValidator()
    {
        RuleFor(image => image.Server).NotEmpty();
        RuleFor(image => image.Name).NotEmpty();
        RuleFor(image => image.Tag).NotEmpty();
    }
}

public sealed class FunctionHandlerSpecValidator : AbstractValidator<FunctionHandlerRequest>
{
    public FunctionHandlerSpecValidator()
    {
        RuleFor(handler => handler.Name).NotEmpty().Matches("^[a-z][a-z0-9-]{0,62}$");
        RuleFor(handler => handler.MaxConcurrencyPerInstance).GreaterThan(0);
        RuleFor(handler => handler)
            .Custom((handler, context) =>
            {
                switch (handler.Trigger)
                {
                    case HttpFunctionTriggerRequest http when !FunctionTriggerRules.IsValidHttpRoute(http.Route) ||
                        http.Methods is null || http.Methods.Count == 0 || http.Methods.Any(method =>
                            !FunctionTriggerRules.AllowedHttpMethods.Contains(method)):
                        context.AddFailure(
                            nameof(handler.Trigger),
                            "HTTP route and at least one supported method are required.");
                        break;
                    case TimerFunctionTriggerRequest timer when !FunctionCronExpression.IsValid(timer.Cron) ||
                        timer.Retry is null || timer.Retry.MaxAttempts is <= 0 or > 10 ||
                        timer.Retry.BackoffSeconds is null ||
                        timer.Retry.BackoffSeconds.Any(delay => delay is < 0 or > 86400):
                        context.AddFailure(nameof(handler.Trigger), "Timer trigger requires cron and retry attempts.");
                        break;
                    case null:
                        context.AddFailure(nameof(handler.Trigger), "Trigger is required.");
                        break;
                }
            });
    }
}
