using System.Collections.Immutable;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Functions.Protocol;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Endpoints;

public static class CreateFunctionApp
{
    public static IEndpointRouteBuilder MapCreateFunctionAppEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (
                IMediator mediator,
                [AsParameters] CreateFunctionAppRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(response => Results.Accepted(value: response));
            })
            .WithName("CreateFunctionApp")
            .Produces<CreateFunctionAppResponse>(StatusCodes.Status202Accepted);
        return app;
    }
}

public sealed record CreateFunctionAppRequest(
    [FromBody] CreateFunctionAppRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<CreateFunctionAppResponse>, ITenantCreateOperation;

public sealed record CreateFunctionAppRequestBody(
    string Name,
    string FunctionRoute,
    string ResourceGroupId,
    FunctionRevisionRequest Spec,
    ImmutableHashSet<string> Tags);

public sealed record FunctionRevisionRequest(
    FunctionImageRequest Image,
    FunctionConfiguration Configuration,
    FunctionResources Resources,
    FunctionScalingPolicy Scaling,
    ImmutableList<FunctionHandlerRequest> Handlers)
{
    public FunctionRevisionSpecInput ToDomain() =>
        new(
            new FunctionImage(Image.Server, Image.Name, Image.Tag),
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
                            TriggerId: string.Empty,
                            http.Route,
                            http.Methods.Select(method => method.ToUpperInvariant()).ToImmutableHashSet(StringComparer.Ordinal),
                            http.Exposure),
                        TimerFunctionTriggerRequest timer => new TimerFunctionTriggerSpec(
                            TriggerId: string.Empty,
                            timer.Cron,
                            "UTC",
                            timer.ConcurrencyPolicy,
                            timer.MisfirePolicy,
                            ScheduleVersion: 1,
                            timer.Retry),
                        _ => throw new InvalidOperationException("Unknown Function trigger request.")
                    }))
                .ToImmutableList());
}

public sealed record FunctionImageRequest(string Server, string Name, string Tag);

public sealed record FunctionHandlerRequest(
    string Name,
    int MaxConcurrencyPerInstance,
    FunctionTriggerRequest Trigger);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HttpFunctionTriggerRequest), "http")]
[JsonDerivedType(typeof(TimerFunctionTriggerRequest), "timer")]
public abstract record FunctionTriggerRequest;

public sealed record HttpFunctionTriggerRequest(
    string Route,
    ImmutableHashSet<string> Methods,
    FunctionExposure Exposure) : FunctionTriggerRequest;

public sealed record TimerFunctionTriggerRequest(
    string Cron,
    TimerConcurrencyPolicy ConcurrencyPolicy,
    TimerMisfirePolicy MisfirePolicy,
    FunctionRetryPolicy Retry) : FunctionTriggerRequest;

public sealed record CreateFunctionAppResponse(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber);

public sealed class CreateFunctionAppHandler(
    IFunctionAppDbContext dbContext,
    IResourceGroupLookup resourceGroupLookup,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider,
    IDeploymentTargetLookup deploymentTargetLookup)
    : IMediatorHandler<CreateFunctionAppRequest, CreateFunctionAppResponse>
{
    public async Task<Result<CreateFunctionAppResponse>> HandleAsync(
        CreateFunctionAppRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetAdmission = await deploymentTargetLookup.ResolveForNewWorkloadAsync(
            deploymentTargetId: null,
            cancellationToken);
        if (!targetAdmission.TryGetValue(out _, out var targetAdmissionFailure))
            return targetAdmissionFailure;

        var placementResult = await resourceGroupLookup.GetPlacementAsync(
            request.TenantId,
            request.Body.ResourceGroupId,
            cancellationToken);
        if (!placementResult.TryGetValue(out var placement, out var placementFailure))
            return placementFailure;

        var created = FunctionAppFactory.Create(
            request.TenantId,
            request.Body.ResourceGroupId,
            placement.KubernetesNamespace,
            request.Body.Name,
            request.Body.FunctionRoute,
            request.Body.Spec.ToDomain(),
            request.Body.Tags,
            timeProvider.GetUtcNow());

        return await quotaAdmissionExecutor.ExecuteAsync<FunctionQuotaAllocation, CreateFunctionAppResponse>(
            request.TenantId,
            FunctionAppQuotaResourceKey.Create(created.FunctionApp.Id),
            new CreateQuotaMutation<FunctionQuotaAllocation>(created.FunctionApp.AdmittedQuota),
            async (session, transactionCancellationToken) =>
            {
                await dbContext.FunctionRouteBindings.InsertOneAsync(
                    session,
                    created.RouteBinding,
                    cancellationToken: transactionCancellationToken);
                await dbContext.FunctionApps.InsertOneAsync(
                    session,
                    created.FunctionApp,
                    cancellationToken: transactionCancellationToken);
                await dbContext.FunctionRevisions.InsertOneAsync(
                    session,
                    created.Revision,
                    cancellationToken: transactionCancellationToken);
                await dbContext.StoreEventAsync(
                    [
                        created.FunctionApp.ToCreatedEvent(created.Revision),
                        created.Revision.ToRevisionCreatedEvent()
                    ],
                    session,
                    transactionCancellationToken);
                return new CreateFunctionAppResponse(
                    created.FunctionApp.Id,
                    created.Revision.Id,
                    created.Revision.RevisionNumber);
            },
            cancellationToken);
    }
}

public sealed class CreateFunctionAppRequestValidator : AbstractValidator<CreateFunctionAppRequest>
{
    public CreateFunctionAppRequestValidator() =>
        RuleFor(request => request.Body).NotNull().SetValidator(new CreateFunctionAppRequestBodyValidator());
}

public sealed class CreateFunctionAppRequestBodyValidator : AbstractValidator<CreateFunctionAppRequestBody>
{
    private static readonly HashSet<string> ReservedRoutes =
        new(StringComparer.OrdinalIgnoreCase) { "api", "functions", "health", "internal", "_pole" };

    public CreateFunctionAppRequestBodyValidator()
    {
        RuleFor(body => body.Name)
            .NotEmpty()
            .MaximumLength(63)
            .Matches("^[a-z][a-z0-9-]{2,62}$");
        RuleFor(body => body.FunctionRoute)
            .NotEmpty()
            .Matches("^[a-z][a-z0-9-]{2,62}$")
            .Must(route => !ReservedRoutes.Contains(route))
            .WithMessage("Function route is reserved.");
        RuleFor(body => body.ResourceGroupId).NotEmpty();
        RuleFor(body => body.Spec).NotNull().SetValidator(new FunctionRevisionSpecInputValidator());
        RuleFor(body => body.Tags).NotNull();
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
        When(spec => spec.Handlers is not null, () =>
        {
            RuleFor(spec => spec.Handlers)
                .Must(handlers => handlers
                    .Select(handler => handler?.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Count() == handlers.Count)
                .WithMessage("Handler names must be unique.");
            RuleForEach(spec => spec.Handlers).NotNull().SetValidator(new FunctionHandlerSpecValidator());
            RuleFor(spec => spec.Handlers)
                .Must(HaveNoOverlappingHttpRoutes)
                .WithMessage("HTTP handler routes and methods must not overlap.");
        });
        When(spec => spec.Handlers is not null && spec.Scaling is not null, () =>
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
        return handlers
            .Where(handler => handler?.Trigger is HttpFunctionTriggerRequest)
            .SelectMany(handler =>
            {
                var trigger = (HttpFunctionTriggerRequest)handler.Trigger;
                return trigger.Methods?.Select(method =>
                    $"{FunctionHandlerSpecValidator.CanonicalizeRoute(trigger.Route)}:{method}") ?? [];
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

public sealed class FunctionConfigurationValidator : AbstractValidator<FunctionConfiguration>
{
    public FunctionConfigurationValidator()
    {
        RuleFor(configuration => configuration.EnvironmentVariables).NotNull();
        RuleFor(configuration => configuration.SecretReferences).NotNull();
        RuleFor(configuration => configuration)
            .Must(HasValidEnvironmentNames)
            .WithMessage("Configuration keys must be valid environment variable names and cannot override Pole runtime settings.");
        RuleFor(configuration => configuration)
            .Must(HasNoDuplicateKeys)
            .WithMessage("Environment variables and secret references cannot use the same key.");
    }

    private static bool HasValidEnvironmentNames(FunctionConfiguration configuration) =>
        configuration.EnvironmentVariables is not null &&
        configuration.SecretReferences is not null &&
        configuration.EnvironmentVariables.Keys
            .Concat(configuration.SecretReferences.Keys)
            .All(IsAllowedEnvironmentName);

    private static bool HasNoDuplicateKeys(FunctionConfiguration configuration) =>
        configuration.EnvironmentVariables is not null &&
        configuration.SecretReferences is not null &&
        !configuration.EnvironmentVariables.Keys.Intersect(
                configuration.SecretReferences.Keys,
                StringComparer.OrdinalIgnoreCase)
            .Any();

    private static bool IsAllowedEnvironmentName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(character => char.IsLetterOrDigit(character) || character == '_') &&
        !name.StartsWith("PoleFunctions__", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("ASPNETCORE_URLS", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("DOTNET_EnableDiagnostics", StringComparison.OrdinalIgnoreCase);
}

public sealed class FunctionResourcesValidator : AbstractValidator<FunctionResources>
{
    public FunctionResourcesValidator()
    {
        RuleFor(resources => resources.Requests).NotNull().SetValidator(new FunctionResourceValuesValidator());
        RuleFor(resources => resources.Limits).NotNull().SetValidator(new FunctionResourceValuesValidator());
        RuleFor(resources => resources).Must(resources =>
                resources.Requests is not null &&
                resources.Limits is not null &&
                resources.Limits.CpuMillis >= resources.Requests.CpuMillis &&
                resources.Limits.MemoryMi >= resources.Requests.MemoryMi)
            .WithMessage("Resource limits cannot be lower than requests.");
    }
}

public sealed class FunctionResourceValuesValidator : AbstractValidator<FunctionResourceValues>
{
    public FunctionResourceValuesValidator()
    {
        RuleFor(resources => resources.CpuMillis).GreaterThan(0);
        RuleFor(resources => resources.MemoryMi).GreaterThan(0);
    }
}

public sealed class FunctionScalingPolicyValidator : AbstractValidator<FunctionScalingPolicy>
{
    public FunctionScalingPolicyValidator()
    {
        RuleFor(scaling => scaling.MinInstances).GreaterThanOrEqualTo(0);
        RuleFor(scaling => scaling.MaxInstances).GreaterThan(0);
        RuleFor(scaling => scaling)
            .Must(value => value.MinInstances <= value.MaxInstances)
            .WithMessage("MinInstances cannot exceed MaxInstances.");
        RuleFor(scaling => scaling.TargetConcurrencyPerInstance).GreaterThan(0);
        RuleFor(scaling => scaling.MaxConcurrencyPerInstance).GreaterThan(0);
        RuleFor(scaling => scaling.ScaleDownDelaySeconds).InclusiveBetween(0, 3600);
        RuleFor(scaling => scaling.ActivationTimeoutSeconds).InclusiveBetween(
            FunctionScalingPolicy.MinimumActivationTimeoutSeconds,
            FunctionScalingPolicy.MaximumActivationTimeoutSeconds);
        RuleFor(scaling => scaling)
            .Must(value => value.TargetConcurrencyPerInstance <= value.MaxConcurrencyPerInstance)
            .WithMessage("Target concurrency cannot exceed maximum concurrency.");
    }
}

public sealed class FunctionHandlerSpecValidator : AbstractValidator<FunctionHandlerRequest>
{
    public FunctionHandlerSpecValidator()
    {
        RuleFor(handler => handler.Name)
            .NotEmpty()
            .Matches("^[a-z][a-z0-9-]{0,62}$");
        RuleFor(handler => handler.MaxConcurrencyPerInstance).GreaterThan(0);
        RuleFor(handler => handler).Custom((handler, context) =>
        {
            switch (handler.Trigger)
            {
                case HttpFunctionTriggerRequest http when
                    !IsValidHttpRoute(http.Route) ||
                    http.Methods is null ||
                    http.Methods.Count == 0 ||
                    http.Methods.Any(method => !AllowedHttpMethods.Contains(method)):
                    context.AddFailure(
                        nameof(handler.Trigger),
                        "HTTP route and at least one supported method are required.");
                    break;
                case TimerFunctionTriggerRequest timer when
                    !FunctionCronExpression.IsValid(timer.Cron) ||
                    timer.Retry is null ||
                    timer.Retry.MaxAttempts is <= 0 or > 10 ||
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

    private static readonly HashSet<string> AllowedHttpMethods =
        new(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], StringComparer.OrdinalIgnoreCase);

    private static bool IsValidHttpRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route) || route.Length > 200 || route.StartsWith('/') || route.EndsWith('/'))
            return false;

        var segments = route.Split('/');
        return segments.All(segment =>
            segment.Length > 0 &&
            segment is not "." and not ".." &&
            (IsLiteralSegment(segment) || IsParameterSegment(segment)));
    }

    internal static string CanonicalizeRoute(string? route) =>
        string.Join('/', (route ?? string.Empty).Split('/').Select(segment =>
            IsParameterSegment(segment) ? "{}" : segment));

    private static bool IsLiteralSegment(string segment) =>
        char.IsLetterOrDigit(segment[0]) &&
        segment.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static bool IsParameterSegment(string segment) =>
        segment.Length >= 3 &&
        segment[0] == '{' &&
        segment[^1] == '}' &&
        char.IsLetter(segment[1]) &&
        segment[1..^1].All(character => char.IsLetterOrDigit(character) || character == '_');

}
