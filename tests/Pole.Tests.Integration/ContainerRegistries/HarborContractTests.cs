using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Harbor;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.ResultType;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Pole.Tests.Integration.ContainerRegistries;

public sealed class HarborContractTests
{
    [HarborContractFact]
    [Trait("Category", "HarborContract")]
    public async Task ProvisionProject_CreatesUsableProjectAndRobotCredentials()
    {
        var options = GetOptions();
        using var handler = new HttpClientHandler { UseCookies = false };
        using var httpClient = new HttpClient(handler);
        var client = new HarborContainerRegistryClient(
            httpClient,
            options);
        var projectName = $"pole-contract-{Guid.NewGuid():N}"[..30];
        var attemptId = Guid.NewGuid().ToString("N");
        ProvisionedRegistryProject? provisioned = null;

        try
        {
            var result = await client.ProvisionProjectAsync(
                new RegistryProjectProvisioningRequest("contract-test", projectName, attemptId));

            if (!result.TryGetValue(out var created, out var failure))
                Assert.Fail(failure.ToString());
            provisioned = created;
            Assert.Equal(projectName, created.ProjectName);
            Assert.False(string.IsNullOrWhiteSpace(created.BuildCredential.Username));
            Assert.False(string.IsNullOrWhiteSpace(created.BuildCredential.Secret));
            Assert.False(string.IsNullOrWhiteSpace(created.PullCredential.Username));
            Assert.False(string.IsNullOrWhiteSpace(created.PullCredential.Secret));
            Assert.NotEqual(created.BuildCredential.Username, created.PullCredential.Username);
        }
        finally
        {
            var cleanup = await client.DeleteProjectAsync(
                provisioned?.ExternalProjectId ?? projectName);
            Assert.IsAssignableFrom<ISuccess>(cleanup);
        }
    }

    [HarborBuildKitContractFact]
    [Trait("Category", "HarborBuildKitContract")]
    public async Task ProvisionedBuildRobot_PushesAnImageWithBuildKit()
    {
        var options = GetOptions();
        using var handler = new HttpClientHandler { UseCookies = false };
        using var httpClient = new HttpClient(handler);
        var registryClient = new HarborContainerRegistryClient(
            httpClient,
            options);
        var projectName = $"pole-buildkit-contract-{Guid.NewGuid():N}"[..30];
        var attemptId = Guid.NewGuid().ToString("N");
        ProvisionedRegistryProject? provisioned = null;
        using var context = new TemporaryDirectory();
        var completed = false;

        try
        {
            var projectResult = await registryClient.ProvisionProjectAsync(
                new RegistryProjectProvisioningRequest("buildkit-contract-test", projectName, attemptId));
            if (!projectResult.TryGetValue(out var created, out var provisioningFailure))
                Assert.Fail(provisioningFailure.ToString());
            provisioned = created;

            var buildKit = new BuildKitClient(
                new ProcessRunner(),
                new BuildKitOptions
                {
                    Enabled = true,
                    Address = Environment.GetEnvironmentVariable("POLE_BUILDKIT_CONTRACT_ADDRESS")!,
                    BuildctlPath = Environment.GetEnvironmentVariable("POLE_BUILDKIT_CONTRACT_PATH")!,
                    TimeoutMinutes = 5
                },
                NullLogger<BuildKitClient>.Instance);
            var target = new RegistryPushTarget(
                options.RegistryHost,
                provisioned.ProjectName,
                "web-services/buildkit-contract",
                $"run-{Guid.NewGuid():N}");

            var buildResult = await buildKit.BuildAndPushAsync(new BuildKitBuildRequest(
                context.Path,
                "FROM scratch\nLABEL pole.contract-test=buildkit\n",
                target,
                provisioned.BuildCredential.Username,
                provisioned.BuildCredential.Secret));

            if (!buildResult.TryGetValue(out var image, out var buildFailure))
                Assert.Fail(buildFailure.ToString());
            Assert.Equal(target.Reference, image.Reference);
            Assert.StartsWith("sha256:", image.Digest, StringComparison.Ordinal);
            completed = true;
        }
        finally
        {
            var cleanup = await registryClient.DeleteProjectAsync(
                provisioned?.ExternalProjectId ?? projectName);
            if (completed)
                Assert.IsAssignableFrom<ISuccess>(cleanup);
        }
    }

    private static HarborOptions GetOptions() =>
        new()
        {
            Enabled = true,
            BaseUrl = Environment.GetEnvironmentVariable("POLE_HARBOR_CONTRACT_BASE_URL")!,
            RegistryHost = Environment.GetEnvironmentVariable("POLE_HARBOR_CONTRACT_REGISTRY_HOST")!,
            Username = Environment.GetEnvironmentVariable("POLE_HARBOR_CONTRACT_USERNAME")!,
            Password = Environment.GetEnvironmentVariable("POLE_HARBOR_CONTRACT_PASSWORD")!,
            RobotDurationDays = -1
        };
}

public sealed class HarborBuildKitContractFactAttribute : FactAttribute
{
    private static readonly string[] RequiredVariables =
    [
        "POLE_HARBOR_CONTRACT_BASE_URL",
        "POLE_HARBOR_CONTRACT_REGISTRY_HOST",
        "POLE_HARBOR_CONTRACT_USERNAME",
        "POLE_HARBOR_CONTRACT_PASSWORD",
        "POLE_BUILDKIT_CONTRACT_ADDRESS",
        "POLE_BUILDKIT_CONTRACT_PATH"
    ];

    public HarborBuildKitContractFactAttribute()
    {
        if (RequiredVariables.Any(name =>
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
        {
            Skip = "Set POLE_HARBOR_CONTRACT_* and POLE_BUILDKIT_CONTRACT_* variables to run against real Harbor and BuildKit instances.";
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pole-buildkit-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}

public sealed class HarborContractFactAttribute : FactAttribute
{
    private static readonly string[] RequiredVariables =
    [
        "POLE_HARBOR_CONTRACT_BASE_URL",
        "POLE_HARBOR_CONTRACT_REGISTRY_HOST",
        "POLE_HARBOR_CONTRACT_USERNAME",
        "POLE_HARBOR_CONTRACT_PASSWORD"
    ];

    public HarborContractFactAttribute()
    {
        if (RequiredVariables.Any(name =>
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
        {
            Skip = "Set the POLE_HARBOR_CONTRACT_* variables to run against a real Harbor instance.";
        }
    }
}
