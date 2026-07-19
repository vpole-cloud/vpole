using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Pole.Features.SourceBuilds.Tests;

public sealed class BuildKitClientTests
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void BuildRequestStringRepresentationRedactsCredentialsAndDockerfile()
    {
        var request = new BuildKitBuildRequest(
            "context",
            "RUN echo dockerfile-secret",
            new RegistryPushTarget("harbor.test", "pole-test", "service", "tag"),
            "sensitive-username",
            "sensitive-registry-secret");

        var diagnostic = request.ToString();

        Assert.DoesNotContain("sensitive-username", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-registry-secret", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("dockerfile-secret", diagnostic, StringComparison.Ordinal);
        Assert.Contains("REDACTED", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_UsesArgumentListTemporaryDockerConfigAndMetadataDigest()
    {
        using var context = new TemporaryDirectory();
        var runner = new RecordingProcessRunner(async spec =>
        {
            Assert.DoesNotContain(spec.Arguments, argument => argument.Contains("registry-secret", StringComparison.Ordinal));
            Assert.Contains(spec.Arguments, argument =>
                argument == "type=image,name=harbor.test/pole-test/web-services/service-1:build-1,push=true");
            var dockerConfig = System.IO.Path.Combine(spec.EnvironmentVariables["DOCKER_CONFIG"], "config.json");
            var configJson = await File.ReadAllTextAsync(dockerConfig);
            Assert.DoesNotContain("registry-secret", configJson, StringComparison.Ordinal);
            Assert.Contains("harbor.test", configJson, StringComparison.Ordinal);

            var metadataIndex = spec.Arguments.ToList().IndexOf("--metadata-file");
            await File.WriteAllTextAsync(
                spec.Arguments[metadataIndex + 1],
                $$"""{ "containerimage.digest": "{{Digest}}" }""");
            return new ProcessResult(0, "build output", string.Empty);
        });
        var client = CreateClient(runner);

        var result = await client.BuildAndPushAsync(new BuildKitBuildRequest(
            context.Path,
            "FROM scratch",
            new RegistryPushTarget("harbor.test", "pole-test", "web-services/service-1", "build-1"),
            "robot$build",
            "registry-secret"));

        Assert.True(result.TryGetValue(out var image, out _));
        Assert.Equal(Digest, image.Digest);
        Assert.NotNull(runner.LastSpec);
        Assert.False(Directory.Exists(runner.LastSpec.EnvironmentVariables["DOCKER_CONFIG"]));
    }

    [Fact]
    public async Task BuildFailure_DoesNotExposeBuildOutput()
    {
        using var context = new TemporaryDirectory();
        var runner = new RecordingProcessRunner(_ => Task.FromResult(
            new ProcessResult(1, "registry-secret from build", "failed to authorize registry-secret")));
        var client = CreateClient(runner);

        var result = await client.BuildAndPushAsync(new BuildKitBuildRequest(
            context.Path,
            "FROM scratch",
            new RegistryPushTarget("harbor.test", "pole-test", "service", "tag"),
            "robot$build",
            "registry-secret"));

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.DoesNotContain("registry-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("could not authenticate", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildFailure_IdentifiesTheTargetRegistryWithoutReflectingItsOutput()
    {
        using var context = new TemporaryDirectory();
        var runner = new RecordingProcessRunner(_ => Task.FromResult(
            new ProcessResult(1, string.Empty, "dial tcp harbor.test:5000: registry-secret")));
        var client = CreateClient(runner);

        var result = await client.BuildAndPushAsync(new BuildKitBuildRequest(
            context.Path,
            "FROM scratch",
            new RegistryPushTarget("harbor.test:5000", "pole-test", "service", "tag"),
            "robot$build",
            "registry-secret"));

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.DoesNotContain("registry-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("configured target registry", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static BuildKitClient CreateClient(IProcessRunner runner) =>
        new(runner, new BuildKitOptions
        {
            Enabled = true,
            Address = "tcp://buildkit.test:1234",
            BuildctlPath = "buildctl",
            TimeoutMinutes = 1
        },
        NullLogger<BuildKitClient>.Instance);

    private sealed class RecordingProcessRunner(
        Func<ProcessSpec, Task<ProcessResult>> callback) : IProcessRunner
    {
        public ProcessSpec? LastSpec { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessSpec spec,
            CancellationToken cancellationToken = default)
        {
            LastSpec = spec;
            return callback(spec);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pole-buildkit-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
