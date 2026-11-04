using Microsoft.Extensions.DependencyInjection;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.Secrets.Tests;

public sealed class ConsumerSecretProjectionResolverTests
{
    [Theory]
    [InlineData("binding")]
    [InlineData("placement")]
    [InlineData("values")]
    public async Task Authority_is_checked_again_after_reconciliation(string change)
    {
        var type = ResourceTypeId.Create("pole/web-service");
        var binding = SecretBinding.Create("binding", type.Value, "consumer", new AllSecretDeployments(), []);
        var current = SecretFactory.Create("secret", "tenant", "group", "scope", "name", "purpose", ["token"], DateTime.UtcNow)
            .Bind(binding, DateTime.UtcNow);
        var placement = new ResourceDeploymentRuntimePlacementDto("deployment", "consumer", "cell", "target", "namespace",
            1, 1, null!, ResourceDeploymentStatus.Applied, ResourceDeploymentDesiredOperation.Apply, "scope");
        LoadExactResourceDeploymentRuntimePlacement loadPlacement = (request, _) => Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(placement);
        LoadSecretById loadSecret = (_, _, _) => Task.FromResult<Secret?>(current);
        LoadSecretProjections loadProjections = (_, _, _) => Task.FromResult<IReadOnlyList<SecretProjection>>([]);
        var reconciled = 0;
        var reconciler = new SecretProjectionReconciler(loadSecret, (_, _, _, _) => throw new InvalidOperationException(),
            loadProjections, (request, _) =>
            {
                reconciled++;
                if (change == "binding") current = current.Detach(binding.Id, DateTime.UtcNow);
                if (change == "placement") placement = placement with { IsolationScopeId = "foreign" };
                if (change == "values") current = current with { KeyNames = [] };
                return Task.FromResult<IReadOnlyList<PlacementSummaryDto>>([]);
            }, loadPlacement, (_, _, _, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(), null!, null!, TimeProvider.System);
        var services = new ServiceCollection();
        services.AddSecretsFeature();
        services.AddSingleton<LoadRegisteredResource>((request, _) => Task.FromResult<RegisteredResource?>(
            new("registration", "tenant", "group", type, "consumer", "consumer", null, DateTime.UtcNow, DateTime.UtcNow)));
        services.AddSingleton(loadPlacement);
        services.AddSingleton(loadSecret);
        services.AddSingleton(loadProjections);
        services.AddSingleton(reconciler);
        using var provider = services.BuildServiceProvider();
        var resolve = provider.GetRequiredService<EnsureConsumerSecretProjections>();

        var outcome = await resolve(
            new EnsureConsumerSecretProjectionsRequest(
                TenantId: "tenant",
                ConsumerType: type,
                ConsumerId: "consumer",
                DeploymentId: "deployment",
                References: [new("secret", "token")]),
            ct: default);

        Assert.Equal(1, reconciled);
        var failure = Assert.IsType<ConsumerSecretReferenceUnavailable>(outcome.Value);
        Assert.Equal("secret", failure.SecretId);
        Assert.Equal(ConsumerSecretReferenceError.SecretBindingUnavailable, failure.Reason);
    }
}
