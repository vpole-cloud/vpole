using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Functions.Protocol;

namespace Pole.Features.Functions.Application.Persistence;

public sealed record FunctionPinnedProfile(
    string Name,
    long Revision);

public sealed record FunctionDeclarationPlacement(
    string DeploymentTarget,
    FunctionPinnedProfile ComputeProfile,
    string? IsolationScope);

public sealed record FunctionDeclarationImage(
    string Server,
    string Name,
    string Tag);

public sealed record FunctionDeclarationSecret(
    string Secret,
    string Key);

public sealed record FunctionDeclarationHandler(
    string Name,
    int MaxConcurrencyPerInstance,
    FunctionDeclarationTrigger Trigger);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FunctionDeclarationHttpTrigger), "http")]
[JsonDerivedType(typeof(FunctionDeclarationTimerTrigger), "timer")]
public closed record FunctionDeclarationTrigger;

public sealed record FunctionDeclarationHttpTrigger(
    string Route,
    ImmutableList<string> Methods,
    [property: JsonRequired] FunctionExposure Exposure) : FunctionDeclarationTrigger;

public sealed record FunctionDeclarationTimerTrigger(
    string Cron,
    TimerConcurrencyPolicy ConcurrencyPolicy,
    TimerMisfirePolicy MisfirePolicy,
    FunctionRetryPolicy Retry) : FunctionDeclarationTrigger;

public sealed record FunctionDeclaration
{
    public required string ResourceGroup { get; init; }
    public required string FunctionRoute { get; init; }
    public required FunctionDeclarationPlacement Placement { get; init; }
    public required FunctionDeclarationImage Image { get; init; }
    public required FunctionResources Resources { get; init; }
    public required FunctionScalingPolicy Scaling { get; init; }
    public required ImmutableList<FunctionDeclarationHandler> Handlers { get; init; }
    public ImmutableDictionary<string, string> Environment { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, FunctionDeclarationSecret> SecretEnvironment { get; init; } =
        ImmutableDictionary<string, FunctionDeclarationSecret>.Empty;
    public string[] Tags { get; init; } = [];
}

internal static class FunctionDeclarationJson
{
    private const string HandlersField = "spec.handlers";

    private static readonly JsonSerializerOptions Options = new(DeclarationJson.Options)
    {
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static FunctionDeclaration Read(JsonElement spec) =>
        spec.Deserialize<FunctionDeclaration>(Options) ??
        throw new DeclarationSchemaException(DeclarationFieldPaths.Spec);

    public static JsonElement Normalize(JsonElement spec)
    {
        var value = Read(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.ResourceGroup), DeclarationFieldPaths.ResourceGroup);
        DeclarationJson.Require(DeclarationNames.Valid(value.FunctionRoute) && value.FunctionRoute.Length >= 3 &&
            value.FunctionRoute is not ("api" or "functions" or "health" or "internal" or "_pole"),
            "spec.functionRoute");
        DeclarationJson.Require(value.Placement is not null &&
            DeclarationNames.Valid(value.Placement.DeploymentTarget), DeclarationFieldPaths.PlacementDeploymentTarget);
        DeclarationJson.Require(value.Placement.ComputeProfile is { Revision: > 0 } profile &&
            DeclarationNames.Valid(profile.Name), DeclarationFieldPaths.PlacementComputeProfile);
        DeclarationJson.Require(value.Placement.IsolationScope is null ||
            DeclarationNames.Valid(value.Placement.IsolationScope) && value.Placement.IsolationScope != "default",
            DeclarationFieldPaths.PlacementIsolationScope);
        DeclarationJson.Require(value.Image is not null &&
            ValidImagePart(value.Image.Server) && ValidImagePart(value.Image.Name) && ValidImagePart(value.Image.Tag),
            DeclarationFieldPaths.Image);
        DeclarationJson.Require(value.Resources is not null &&
            new FunctionResourcesValidator().Validate(value.Resources).IsValid, "spec.resources");
        DeclarationJson.Require(value.Scaling is not null &&
            new FunctionScalingPolicyValidator().Validate(value.Scaling).IsValid, "spec.scaling");
        DeclarationJson.Require(value.Environment is not null && value.SecretEnvironment is not null &&
            value.Environment.Values.All(item => item is not null) && value.SecretEnvironment.Values.All(item =>
                item is not null && DeclarationNames.Valid(item.Secret) && SecretKeyReferenceRules.ValidKey(item.Key)),
            DeclarationFieldPaths.Environment);
        DeclarationJson.Require(value.Environment.Keys.Concat(value.SecretEnvironment.Keys)
            .All(FunctionConfigurationValidator.IsAllowedEnvironmentName) &&
            !value.Environment.Keys.Intersect(value.SecretEnvironment.Keys, StringComparer.OrdinalIgnoreCase).Any(),
            DeclarationFieldPaths.Environment);
        DeclarationJson.Require(value.Handlers is { Count: > 0 } &&
            value.Handlers.All(handler => ValidHandler(handler, value.Scaling.MaxConcurrencyPerInstance)) &&
            value.Handlers.Select(handler => handler.Name).Distinct(StringComparer.Ordinal).Count() ==
            value.Handlers.Count, HandlersField);
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DeclarationJson.Require(value.Handlers.All(handler =>
            handler.Trigger is not FunctionDeclarationHttpTrigger http ||
            http.Methods.All(method => routes.Add($"{FunctionTriggerRules.CanonicalizeRoute(http.Route)}:{method}"))),
            HandlersField);
        DeclarationJson.Require(value.Tags is not null && value.Tags.All(tag => !string.IsNullOrWhiteSpace(tag)),
            DeclarationFieldPaths.Tags);
        return JsonSerializer.SerializeToElement(value with
        {
            Handlers = value.Handlers.OrderBy(handler => handler.Name, StringComparer.Ordinal)
                .Select(handler => handler.Trigger is FunctionDeclarationHttpTrigger http
                    ? handler with
                    {
                        Trigger = http with
                        {
                            Methods = http.Methods.Select(method => method.ToUpperInvariant())
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal).ToImmutableList()
                        }
                    } : handler).ToImmutableList(),
            Tags = value.Tags.Select(tag => tag.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .ToArray()
        }, Options);
    }

    private static bool ValidImagePart(string? value) =>
        value is { Length: > 0 and <= 255 } && !value.Any(char.IsWhiteSpace);

    private static bool ValidHandler(FunctionDeclarationHandler? handler, int maximum) =>
        handler is not null && DeclarationNames.Valid(handler.Name) && handler.MaxConcurrencyPerInstance > 0 &&
        handler.MaxConcurrencyPerInstance <= maximum && handler.Trigger switch
        {
            FunctionDeclarationHttpTrigger http => FunctionTriggerRules.IsValidHttpRoute(http.Route) &&
                http.Methods is { Count: > 0 } && http.Methods.All(FunctionTriggerRules.AllowedHttpMethods.Contains) &&
                Enum.IsDefined(http.Exposure),
            FunctionDeclarationTimerTrigger timer => FunctionCronExpression.IsValid(timer.Cron) &&
                Enum.IsDefined(timer.ConcurrencyPolicy) && Enum.IsDefined(timer.MisfirePolicy) &&
                timer.Retry is { MaxAttempts: > 0 and <= 10, BackoffSeconds: not null } &&
                timer.Retry.BackoffSeconds.All(delay => delay is >= 0 and <= 86400),
            null => false
        };

    public static FunctionRevisionSpec Spec(FunctionDeclaration declaration,
        ImmutableDictionary<string, FunctionSecretReference> secrets, FunctionImage? currentImage) => new(
        new FunctionImage(declaration.Image.Server, declaration.Image.Name, declaration.Image.Tag,
            Digest: currentImage is not null && currentImage.Server == declaration.Image.Server &&
                currentImage.Name == declaration.Image.Name && currentImage.Tag == declaration.Image.Tag
                    ? currentImage.Digest : null,
            PullSecretName: currentImage?.PullSecretName),
        PoleFunctionProtocol.Version,
        new(declaration.Environment, secrets), declaration.Resources, declaration.Scaling,
        declaration.Handlers.Select(handler => new FunctionHandlerSpec(handler.Name,
            handler.MaxConcurrencyPerInstance, handler.Trigger switch
            {
                FunctionDeclarationHttpTrigger http => new HttpFunctionTriggerSpec("", http.Route,
                    http.Methods.Select(method => method.ToUpperInvariant()).ToImmutableHashSet(), http.Exposure),
                FunctionDeclarationTimerTrigger timer => new TimerFunctionTriggerSpec("", timer.Cron, "UTC",
                    timer.ConcurrencyPolicy, timer.MisfirePolicy, 1, timer.Retry)
            })).ToImmutableList());
}
