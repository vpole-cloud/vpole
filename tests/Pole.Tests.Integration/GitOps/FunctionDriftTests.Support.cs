using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Compiler;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected IMongoCollection<FunctionApp> FunctionApps =>
        Fixture.Database.GetCollection<FunctionApp>("FunctionApps");

    private protected IMongoCollection<FunctionRevision> FunctionRevisions =>
        Fixture.Database.GetCollection<FunctionRevision>("FunctionRevisions");

    private protected IMongoCollection<FunctionRouteBinding> FunctionRoutes =>
        Fixture.Database.GetCollection<FunctionRouteBinding>("FunctionRouteBindings");

    private protected Task SeedFunctionPlanningCatalog() => Fixture.PrepareNamedCatalogAsync();

    private protected ManifestFile[] FunctionSnapshot(
        bool secrets = false,
        string route = "function-worker",
        string? scope = "function-scope") =>
    [
        new(
            "group.yaml",
            """
            apiVersion: pole.io/v1alpha1
            kind: ResourceGroup
            metadata: {name: function-group}
            spec: {description: Functions}
            ---
            apiVersion: pole.io/v1alpha1
            kind: IsolationScope
            metadata: {name: function-scope}
            spec: {resourceGroup: function-group}
            """),
        new(
            "function.yaml",
            $$"""
              apiVersion: pole.io/v1alpha1
              kind: FunctionApp
              metadata: {name: function-worker}
              spec:
                resourceGroup: function-group
                functionRoute: {{route}}-{{Data.TenantId}}
                placement:
                  deploymentTarget: lab
                  computeProfile: {name: small, revision: 1}
                  {{(scope is null ? "" : $"isolationScope: {scope}")}}
                image: {server: example.test, name: function, tag: one}
                environment: {APP_MODE: production}
                {{(secrets ? "secretEnvironment: {STAGE_FIVE_SECRET: {secret: function-secret, key: password}}" : "")}}
                resources:
                  requests: {cpuMillis: 100, memoryMi: 256}
                  limits: {cpuMillis: 500, memoryMi: 512}
                scaling:
                  minInstances: 0
                  maxInstances: 2
                  targetConcurrencyPerInstance: 1
                  maxConcurrencyPerInstance: 10
                  scaleDownDelaySeconds: 60
                  activationTimeoutSeconds: 120
                handlers:
                  - name: hello
                    maxConcurrencyPerInstance: 10
                    trigger: {route: hello, methods: [GET], exposure: Internal, type: http}
                  - name: clock
                    maxConcurrencyPerInstance: 1
                    trigger:
                      type: timer
                      cron: '*/5 * * * *'
                      concurrencyPolicy: Forbid
                      misfirePolicy: Skip
                      retry: {maxAttempts: 2, backoffSeconds: [5]}
                tags: [functions]
              """),
        .. secrets
            ? new[]
            {
                new ManifestFile(
                    "secret.yaml",
                    $$"""
                      apiVersion: pole.io/v1alpha1
                      kind: Secret
                      metadata: {name: function-secret}
                      spec:
                        resourceGroup: function-group
                        {{(scope is null ? "" : $"isolationScope: {scope}")}}
                        keys: [password]
                      ---
                      apiVersion: pole.io/v1alpha1
                      kind: SecretBinding
                      metadata: {name: function-access}
                      spec:
                        secret: function-secret
                        consumer: {kind: FunctionApp, name: function-worker}
                      """),
            }
            : [],
    ];

    private protected async Task<FunctionApp> CurrentFunction() => await FunctionApps
        .Find(Builders<FunctionApp>.Filter.Eq(item => item.TenantId, Data.TenantId))
        .SingleAsync();

    private protected async Task FailFunctionRuntime(FunctionApp app)
    {
        var failed = await Services.GetRequiredService<FailFunctionRevision>()(
            app.TenantId,
            app.Id,
            app.PendingRevisionId!,
            new FunctionRevisionFailure("TestRuntimeFailure", "Runtime failure"));
        Assert.IsType<FunctionRevisionLifecycleCompleted>(failed.Value);
    }
}
