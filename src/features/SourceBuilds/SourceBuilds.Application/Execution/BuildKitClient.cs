using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Execution;

public sealed record BuildKitBuildRequest(
    string ContextPath,
    string Dockerfile,
    RegistryPushTarget Target,
    string RegistryUsername,
    string RegistrySecret);

public sealed record BuiltContainerImage(string Reference, string Digest);

public interface IBuildKitClient
{
    Task<Result<BuiltContainerImage>> BuildAndPushAsync(
        BuildKitBuildRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class BuildKitClient(
    IProcessRunner processRunner,
    BuildKitOptions options) : IBuildKitClient
{
    public async Task<Result<BuiltContainerImage>> BuildAndPushAsync(
        BuildKitBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
        {
            return Failures.ExternalService(
                "Container image building is not configured.",
                "BuildKit",
                "BuildKit configuration is disabled or incomplete.");
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
                process = await processRunner.RunAsync(spec, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failures.ExternalService(
                    "BuildKit timed out while building the container image.",
                    "BuildKit",
                    $"Timeout: {options.TimeoutMinutes} minutes.");
            }
            catch (Exception exception) when (exception is Win32Exception or IOException)
            {
                return Failures.ExternalService(
                    "BuildKit could not be started.",
                    "BuildKit",
                    "The buildctl executable or BuildKit endpoint is unavailable.");
            }

            if (process.ExitCode != 0)
            {
                return Failures.ExternalService(
                    "BuildKit failed to build or push the container image.",
                    "BuildKit",
                    $"buildctl exited with code {process.ExitCode}.");
            }

            var digest = await ReadDigestAsync(metadataPath, cancellationToken);
            if (digest is null)
            {
                return Failures.ExternalService(
                    "BuildKit did not return a valid image digest.",
                    "BuildKit",
                    "The metadata file did not contain containerimage.digest.");
            }

            return new BuiltContainerImage(request.Target.Reference, digest);
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
}
