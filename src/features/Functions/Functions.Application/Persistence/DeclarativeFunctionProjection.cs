using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.Functions.Application.Persistence;

internal sealed partial class DeclarativeFunctionProvider
{
    private static DeclarationProjection Project(DeclarationPhase phase, FunctionProjectionState state) => new(
        DeclarationJson.Write(new
        {
            state.ResourceGroupId,
            state.IsolationScopeId,
            state.DeploymentTargetId,
            state.FunctionRoute
        }),
        phase == DeclarationPhase.Prepare || state.Spec is null
            ? DeclarationJson.Write(new { })
            : DeclarationJson.Write(new
            {
                tags = state.Tags.Order(StringComparer.Ordinal),
                configuration = ManagedSpec(state.Spec),
                state.Compute.ComputeProfileId,
                state.Compute.ComputeProfileRevision,
                compute = ComputeProjection(state.Compute)
            }));

    private static JsonElement ManagedSpec(FunctionRevisionSpec spec) => DeclarationJson.Write(new
    {
        image = new { spec.Image.Server, spec.Image.Name, spec.Image.Tag },
        environment = spec.Configuration.EnvironmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        secretEnvironment = spec.Configuration.SecretReferences.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        spec.Resources,
        spec.Scaling,
        handlers = spec.Handlers.OrderBy(handler => handler.Name, StringComparer.Ordinal).Select(handler => new
        {
            handler.Name,
            handler.MaxConcurrencyPerInstance,
            trigger = handler.Trigger switch
            {
                HttpFunctionTriggerSpec http => DeclarationJson.Write(new
                {
                    type = "http",
                    http.Route,
                    methods = http.Methods.Select(method => method.ToUpperInvariant()).Order(StringComparer.Ordinal),
                    exposure = http.Exposure.ToString()
                }),
                TimerFunctionTriggerSpec timer => DeclarationJson.Write(new
                {
                    type = "timer",
                    timer.Cron,
                    concurrencyPolicy = timer.ConcurrencyPolicy.ToString(),
                    misfirePolicy = timer.MisfirePolicy.ToString(),
                    timer.Retry
                }),
                _ => throw new ArgumentOutOfRangeException(nameof(handler))
            }
        })
    });

    private static string ManagedSpecHash(FunctionRevisionSpec spec) => Hash(ManagedSpec(spec));

    private static string PlanningRootHash(FunctionApp app) => Hash(DeclarationJson.Write(new
    {
        app.Name,
        app.NormalizedName,
        app.ResourceGroupId,
        app.Placement,
        app.FunctionRoute,
        app.DesiredState,
        app.AdmittedQuota,
        tags = app.Tags.Order(StringComparer.Ordinal)
    }));

    private static string Hash(JsonElement value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));
}

internal sealed record FunctionProjectionState(
    string ResourceGroupId,
    string IsolationScopeId,
    string DeploymentTargetId,
    string FunctionRoute,
    ResourceDeploymentComputeConfiguration Compute,
    FunctionRevisionSpec? Spec,
    IEnumerable<string> Tags);
