// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

public sealed class DeploymentTargetBootstrapNetworkSourcesTests
{
    private const string Prefix = "DeploymentTargets:Bootstrap:WorkloadConfiguration:NetworkSources:";

    [Fact]
    public void ConfigurationBinding_MapsTypedSourcesAndLeavesUnconfiguredPurposeAbsent()
    {
        var settings = ValidSettings();
        settings[Prefix + "Cidrs:functions/gateway"] = "192.0.2.9/32";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = configuration.GetSection(DeploymentTargetOptions.SectionName).Get<DeploymentTargetOptions>()!;

        Assert.True(new DeploymentTargetOptionsValidator().Validate(Options.DefaultName, options).Succeeded);
        var sources = options.Bootstrap.WorkloadConfiguration.NetworkSources.ToDomain();
        var ingress = Assert.IsType<DeploymentTargetPodNetworkSource>(sources.Get("public-ingress"));
        Assert.Equal("kube-system", ingress.Namespace);
        Assert.Equal("traefik", ingress.PodLabels["app.kubernetes.io/name"]);
        Assert.Equal(
            "192.0.2.9/32",
            Assert.IsType<DeploymentTargetCidrNetworkSource>(sources.Get("functions/gateway")).Cidr);
        Assert.Null(sources.Get("postgres/operator"));

        options.Bootstrap.WorkloadConfiguration.NetworkSources.Pods["public-ingress"].PodLabels.Clear();
        Assert.Equal("traefik", ingress.PodLabels["app.kubernetes.io/name"]);
    }

    [Theory]
    [InlineData("Pods:public-ingress:Namespace", "bad.namespace")]
    [InlineData("Pods:public-ingress:PodLabels:app.kubernetes.io/name", " padded ")]
    [InlineData("Cidrs:functions/gateway", "0.0.0.0/0")]
    [InlineData("Cidrs:public-ingress", "192.0.2.9/32")]
    [InlineData("Cidrs:999", "192.0.2.9/32")]
    public async Task InvalidSourceConfiguration_FailsAtStartup(string key, string value)
    {
        var settings = ValidSettings();
        settings[Prefix + key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddOptions<DeploymentTargetOptions>()
                    .Bind(configuration.GetSection(DeploymentTargetOptions.SectionName))
                    .ValidateOnStart();
                services.AddSingleton<IValidateOptions<DeploymentTargetOptions>, DeploymentTargetOptionsValidator>();
            })
            .Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(failure.Failures, item => item.Contains("NetworkSources", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultBootstrap_DoesNotInferNetworkSources()
    {
        var sources = new DeploymentTargetOptions().Bootstrap.WorkloadConfiguration.NetworkSources.ToDomain();

        Assert.Null(sources.Get("public-ingress"));
        Assert.Null(sources.Get("functions/gateway"));
        Assert.Null(sources.Get("postgres/operator"));
    }

    private static Dictionary<string, string?> ValidSettings() => new()
    {
        [Prefix + "Pods:public-ingress:Namespace"] = "kube-system",
        [Prefix + "Pods:public-ingress:PodLabels:app.kubernetes.io/name"] = "traefik",
    };
}
