using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Harbor;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.ResultType;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.Integration.ContainerRegistries;

[Collection(HarborContractCollection.Name)]
public sealed class HarborContractTests(HarborContractFixture fixture)
{
    [HarborContractFact]
    [Trait("Category", "HarborContract")]
    public async Task ProvisionProject_CreatesUsableProjectAndRobotCredentials()
    {
        var options = fixture.HarborOptions;
        using var handler = new HttpClientHandler { UseCookies = false };
        using var httpClient = new HttpClient(handler);
        var client = new HarborContainerRegistryClient(
            httpClient,
            options);
        var projectName = $"pole-contract-{Guid.NewGuid():N}"[..30];
        var attemptId = Guid.NewGuid().ToString("N");
        await using var resources = new TestResourceScope("Harbor provisioning contract");
        resources.Defer($"delete Harbor project '{projectName}'", async token =>
        {
            var cleanup = await client.DeleteProjectAsync(projectName, token);
            Assert.True(TypedResultExtensions.TryGetValue(cleanup).Succeeded);
        });

        var result = await client.ProvisionProjectAsync(
            new RegistryProjectProvisioningRequest("contract-test", projectName, attemptId));

        if (!result.TryGetValue(out var created, out var failure))
            Assert.Fail(failure.ToString());
        Assert.Equal(projectName, created.ProjectName);
        Assert.False(string.IsNullOrWhiteSpace(created.BuildCredential.Username));
        Assert.False(string.IsNullOrWhiteSpace(created.BuildCredential.Secret));
        Assert.False(string.IsNullOrWhiteSpace(created.PullCredential.Username));
        Assert.False(string.IsNullOrWhiteSpace(created.PullCredential.Secret));
        Assert.NotEqual(created.BuildCredential.Username, created.PullCredential.Username);
    }

    [HarborBuildKitContractFact]
    [Trait("Category", "HarborBuildKitContract")]
    public async Task ProvisionedBuildRobot_PushesAnImageWithBuildKit()
    {
        var options = fixture.HarborOptions;
        using var handler = new HttpClientHandler { UseCookies = false };
        using var httpClient = new HttpClient(handler);
        var registryClient = new HarborContainerRegistryClient(
            httpClient,
            options);
        var projectName = $"pole-buildkit-contract-{Guid.NewGuid():N}"[..30];
        var attemptId = Guid.NewGuid().ToString("N");
        using var context = new TemporaryDirectory();
        var completed = false;
        await using var resources = new TestResourceScope("Harbor BuildKit contract");
        resources.Defer($"delete Harbor project '{projectName}'", async token =>
        {
            var cleanup = await registryClient.DeleteProjectAsync(projectName, token);
            if (completed)
                Assert.True(TypedResultExtensions.TryGetValue(cleanup).Succeeded);
        });

        var projectResult = await registryClient.ProvisionProjectAsync(
            new RegistryProjectProvisioningRequest("buildkit-contract-test", projectName, attemptId));
        if (!projectResult.TryGetValue(out var provisioned, out var provisioningFailure))
            Assert.Fail(provisioningFailure.ToString());

        var processRunner = new ProcessRunner();
        var buildKit = new BuildKitClient(
            processRunner.InvokeAsync,
            new BuildKitOptions
            {
                Enabled = true,
                Address = fixture.BuildKitAddress,
                BuildctlPath = fixture.BuildctlPath,
                TimeoutMinutes = 5
            },
            NullLogger<BuildKitClient>.Instance);
        var target = new RegistryPushTarget(
            options.RegistryHost,
            provisioned.ProjectName,
            "web-services/buildkit-contract",
            $"run-{Guid.NewGuid():N}");

        var buildResult = await buildKit.InvokeAsync(new BuildKitBuildRequest(
            context.Path,
            "FROM scratch\nLABEL pole.contract-test=buildkit\n",
            target,
            provisioned.BuildCredential.Username,
            provisioned.BuildCredential.Secret));

        var build = TypedResultExtensions.TryGetValue(buildResult);
        if (!build.Succeeded)
            Assert.Fail(build.Error.ToString());
        Assert.Equal(target.Reference, build.Value!.Reference);
        Assert.StartsWith("sha256:", build.Value.Digest, StringComparison.Ordinal);
        completed = true;
    }

}

public sealed class HarborBuildKitContractFactAttribute : FactAttribute
{
    public HarborBuildKitContractFactAttribute()
    {
        Skip = KubernetesLabSelection.GetOptInSkipReason("harbor", "buildkit");
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
    public HarborContractFactAttribute()
    {
        Skip = KubernetesLabSelection.GetOptInSkipReason("harbor");
    }
}

public sealed class HarborContractFixture : IAsyncLifetime
{
    private readonly KubernetesLabState _state;
    private KubernetesPortForwardSession? _harborForward;
    private KubernetesPortForwardSession? _buildKitForward;

    public HarborContractFixture()
    {
        var selection = KubernetesLabSelection.RequireSelection();
        _state = selection.State;
    }

    public HarborOptions HarborOptions { get; private set; } = null!;
    public string BuildKitAddress { get; private set; } = null!;
    public string BuildctlPath => _state.BuildctlPath
        ?? throw new InvalidOperationException("The selected lab does not expose buildctl.");

    public async Task InitializeAsync()
    {
        var services = _state.Services
                       ?? throw new InvalidOperationException("The selected lab does not expose Harbor and BuildKit services.");
        var credentials = JsonSerializer.Deserialize<Dictionary<string, string>>(
                              await File.ReadAllTextAsync(services.Harbor.CredentialsPath))
                          ?? throw new InvalidOperationException("The lab Harbor credentials file is malformed.");
        if (!credentials.TryGetValue("username", out var username) ||
            !credentials.TryGetValue("password", out var password) ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("The lab Harbor credentials file is incomplete.");
        }
        if (services.Harbor.TargetKey != services.BuildKit.TargetKey)
            throw new InvalidOperationException("The lab Harbor and BuildKit services must use the same target.");
        var serviceTarget = _state.GetRequiredTarget(services.Harbor.TargetKey);
        _harborForward = await KubernetesPortForwardSession.StartAsync(
            serviceTarget,
            services.Harbor.Namespace,
            $"service/{services.Harbor.Service}",
            services.Harbor.Port);
        try
        {
            _buildKitForward = await KubernetesPortForwardSession.StartAsync(
                serviceTarget,
                services.BuildKit.Namespace,
                $"service/{services.BuildKit.Service}",
                services.BuildKit.Port);
        }
        catch
        {
            await _harborForward.DisposeAsync();
            _harborForward = null;
            throw;
        }

        HarborOptions = new HarborOptions
        {
            Enabled = true,
            BaseUrl = $"http://127.0.0.1:{_harborForward.LocalPort}",
            RegistryHost = services.Harbor.RegistryHost,
            Username = username,
            Password = password,
            RobotDurationDays = -1
        };
        BuildKitAddress = $"tcp://127.0.0.1:{_buildKitForward.LocalPort}";
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_buildKitForward is not null)
                await _buildKitForward.DisposeAsync();
        }
        finally
        {
            if (_harborForward is not null)
                await _harborForward.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HarborContractCollection : ICollectionFixture<HarborContractFixture>
{
    public const string Name = "Kubernetes lab Harbor contracts";
}
