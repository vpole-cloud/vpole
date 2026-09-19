using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Execution;

public sealed record BuildKitBuildRequest(
    string ContextPath,
    string Dockerfile,
    RegistryPushTarget Target,
    string RegistryUsername,
    string RegistrySecret)
{
    public override string ToString() =>
        $"BuildKitBuildRequest(context={ContextPath}, target={Target.Reference}, credentials=[REDACTED])";
}

public sealed record BuiltContainerImage(
    string Reference,
    string Digest);

public sealed record BuildKitNotConfigured;

public sealed record BuildKitTimedOut(int TimeoutMinutes);

public sealed record BuildKitUnavailable;

public sealed record BuildKitBuildRejected(string Classification);

public union BuildContainerImageError(
    BuildKitNotConfigured,
    BuildKitTimedOut,
    BuildKitUnavailable,
    BuildKitBuildRejected);

public delegate Task<Result<BuiltContainerImage, BuildContainerImageError>>
    BuildContainerImage(
        BuildKitBuildRequest request,
        CancellationToken cancellationToken = default);

public sealed class BuildKitClient(
    RunProcess runProcess,
    BuildKitOptions options,
    ILogger<BuildKitClient> logger)
{
    public async Task<Result<BuiltContainerImage, BuildContainerImageError>> InvokeAsync(
        BuildKitBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
        {
            return new Err<BuildContainerImageError>(new(new BuildKitNotConfigured()));
        }

        var operationRoot = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(request.ContextPath))!,
            $"buildkit-{Guid.NewGuid():N}");
        var definitionPath = Path.Combine(operationRoot, "definition");
        var dockerConfigPath = Path.Combine(operationRoot, "docker-config");
        var metadataPath = Path.Combine(operationRoot, "metadata.json");
        Directory.CreateDirectory(definitionPath);
        Directory.CreateDirectory(dockerConfigPath);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(definitionPath, "Dockerfile"),
                request.Dockerfile,
                Encoding.UTF8,
                cancellationToken);
            await WriteDockerConfigAsync(
                dockerConfigPath,
                request.Target.RegistryHost,
                request.RegistryUsername,
                request.RegistrySecret,
                cancellationToken);

            var spec = new ProcessSpec(
                options.BuildctlPath,
                [
                    "--addr", options.Address,
                    "build",
                    "--progress=plain",
                    "--frontend=dockerfile.v0",
                    "--local", $"context={Path.GetFullPath(request.ContextPath)}",
                    "--local", $"dockerfile={definitionPath}",
                    "--opt", "filename=Dockerfile",
                    "--output", $"type=image,name={request.Target.Reference},push=true",
                    "--metadata-file", metadataPath
                ],
                operationRoot,
                new Dictionary<string, string>
                {
                    ["DOCKER_CONFIG"] = dockerConfigPath,
                    ["NO_COLOR"] = "1"
                });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(options.TimeoutMinutes));

            ProcessResult process;
            try
            {
                process = await runProcess(spec, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new Err<BuildContainerImageError>(
                    new(new BuildKitTimedOut(options.TimeoutMinutes)));
            }
            catch (Exception exception) when (exception is Win32Exception or IOException)
            {
                return new Err<BuildContainerImageError>(new(new BuildKitUnavailable()));
            }

            if (process.ExitCode != 0)
            {
                var classification = ClassifyFailure(request, process);
                logger.LogError(
                    "[buildkit-build-failed] buildctl exited with code {ExitCode}. {Diagnostic}",
                    process.ExitCode,
                    classification);
                return new Err<BuildContainerImageError>(
                    new(new BuildKitBuildRejected(classification)));
            }

            var digest = await ReadDigestAsync(metadataPath, cancellationToken);
            if (digest is null)
            {
                return new Err<BuildContainerImageError>(
                    new(new BuildKitBuildRejected("BuildKit did not return a valid image digest.")));
            }

            return new Ok<BuiltContainerImage>(
                new BuiltContainerImage(request.Target.Reference, digest));
        }
        finally
        {
            try
            {
                if (Directory.Exists(operationRoot))
                    Directory.Delete(operationRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task WriteDockerConfigAsync(
        string dockerConfigPath,
        string registryHost,
        string username,
        string secret,
        CancellationToken cancellationToken)
    {
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{secret}"));
        var configFile = Path.Combine(dockerConfigPath, "config.json");
        await using (var stream = new FileStream(
                         configFile,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                new { auths = new Dictionary<string, object> { [registryHost] = new { auth } } },
                cancellationToken: cancellationToken);
        }

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(configFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task<string?> ReadDigestAsync(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(metadataPath))
            return null;
        await using var metadata = File.OpenRead(metadataPath);
        using var document = await JsonDocument.ParseAsync(metadata, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("containerimage.digest", out var digestElement))
            return null;
        var digest = digestElement.GetString();
        return IsSha256Digest(digest) ? digest : null;
    }

    private static bool IsSha256Digest(string? digest) =>
        digest is { Length: 71 } &&
        digest.StartsWith("sha256:", StringComparison.Ordinal) &&
        digest[7..].All(Uri.IsHexDigit);

    private string ClassifyFailure(BuildKitBuildRequest request, ProcessResult process)
    {
        var output = string.Concat(process.StandardError, "\n", process.StandardOutput);
        if (ContainsAny(output, "unauthorized", "forbidden", "denied", "failed to authorize", "no basic auth credentials"))
            return "BuildKit could not authenticate to a container registry.";
        if (ContainsAny(output, "failed to resolve source metadata", "failed to pull", "pull access denied"))
            return "BuildKit could not pull a required base image.";
        if (ContainsAny(output, "dockerfile parse error", "failed to read dockerfile", "unknown instruction"))
            return "BuildKit rejected the generated Dockerfile.";
        if (ContainsAny(output, "failed to compute cache key", "failed to read build context", "context canceled"))
            return "BuildKit could not read the build context.";
        var buildKitAuthority = Uri.TryCreate(options.Address, UriKind.Absolute, out var buildKitEndpoint)
            ? buildKitEndpoint.Authority
            : options.Address;
        if (output.Contains(buildKitAuthority, StringComparison.OrdinalIgnoreCase) &&
            ContainsAny(output, "connection refused", "no such host", "i/o timeout", "network is unreachable", "dial tcp", "x509"))
        {
            return "BuildKit could not reach its configured endpoint.";
        }
        if (output.Contains(request.Target.RegistryHost, StringComparison.OrdinalIgnoreCase) &&
            ContainsAny(output, "connection refused", "no such host", "i/o timeout", "network is unreachable", "dial tcp", "x509"))
        {
            return "BuildKit could not reach the configured target registry.";
        }
        if (ContainsAny(output, "connection refused", "no such host", "i/o timeout", "network is unreachable", "dial tcp", "x509"))
            return "BuildKit could not reach a required container registry.";
        return "BuildKit returned an unclassified build error.";
    }

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
