// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Pole.Testing;

/// <summary>A real, authenticated SDK application published only into the selected local test lab.</summary>
public sealed class FunctionSdkTestImage : IAsyncDisposable
{
    internal const string SdkVersion = "0.1.0-preview.1";

    private const string ApplicationSource = """
                                             using Pole.Functions.Sdk;

                                             var builder = WebApplication.CreateBuilder(args);
                                             builder.Services
                                                 .AddPoleFunctions(builder.Configuration.GetSection(PoleFunctionsOptions.SectionName))
                                                 .AddHttpHandler("hello", (request, context, cancellationToken) =>
                                                     ValueTask.FromResult(PoleHttpResponse.Text(
                                                         string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STAGE_FIVE_SECRET"))
                                                             ? "missing"
                                                             : "configured")));

                                             var app = builder.Build();
                                             app.MapPoleFunctionsAgent();
                                             app.Run();
                                             """;

    private const string Dockerfile = """
                                      FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
                                      WORKDIR /source
                                      COPY . .
                                      RUN dotnet publish FunctionFixture.csproj -c Release -o /app --nologo
                                      FROM mcr.microsoft.com/dotnet/aspnet:10.0
                                      WORKDIR /app
                                      COPY --from=build /app .
                                      ENV ASPNETCORE_HTTP_PORTS=8080
                                      USER $APP_UID
                                      ENTRYPOINT ["dotnet", "FunctionFixture.dll"]
                                      """;

    private readonly TestResourceScope resources;

    private FunctionSdkTestImage(
        TestResourceScope resources,
        string registryHost,
        string project)
    {
        this.resources = resources;
        RegistryHost = registryHost;
        Repository = $"{project}/function-sdk";
        Tag = $"sdk-{SdkVersion}";
    }

    public string RegistryHost { get; }
    public string Repository { get; }
    public string Tag { get; }
    public string Reference => $"{RegistryHost}/{Repository}:{Tag}";

    private static string ProjectFile =>
        $$"""
          <Project Sdk="Microsoft.NET.Sdk.Web">
            <PropertyGroup>
              <TargetFramework>net10.0</TargetFramework>
              <ImplicitUsings>enable</ImplicitUsings>
              <Nullable>enable</Nullable>
            </PropertyGroup>
            <ItemGroup>
              <PackageReference Include="Pole.Functions.Sdk" Version="{{SdkVersion}}" />
            </ItemGroup>
          </Project>
          """;

    public ValueTask DisposeAsync() => resources.DisposeAsync();

    public static async Task<FunctionSdkTestImage> CreateAsync(
        SelectedKubernetesLab lab,
        TestRunIdentity identity,
        CancellationToken cancellationToken)
    {
        if (lab.State.Profile != "full" || lab.State.Services is not { } services ||
            string.IsNullOrWhiteSpace(lab.State.BuildctlPath))
            throw new InvalidOperationException("The Function SDK image requires the selected full Kubernetes lab.");

        var resources = new TestResourceScope("Function SDK test image");
        var project = identity.CreateNamespaceName("func-sdk");
        var image = new FunctionSdkTestImage(resources, services.Harbor.RegistryHost, project);
        try
        {
            var credentials = await ReadCredentialsAsync(services.Harbor.CredentialsPath, cancellationToken);
            var forward = await resources.CreateAsync(
                "Harbor port-forward",
                token => KubernetesPortForwardSession.StartAsync(
                    lab.State.GetRequiredTarget(services.Harbor.TargetKey),
                    services.Harbor.Namespace,
                    $"service/{services.Harbor.Service}",
                    services.Harbor.Port,
                    token),
                (session, _) => session.DisposeAsync().AsTask(),
                cancellationToken);
            var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{forward.LocalPort}/api/v2.0/"),
                Timeout = TimeSpan.FromSeconds(30),
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials.Basic);
            resources.Defer(
                "Harbor client",
                _ =>
                {
                    http.Dispose();
                    return Task.CompletedTask;
                });

            // Register before the request: a response can be lost after Harbor commits the project.
            // A conflict never grants ownership of a pre-existing project.
            var ownsProject = true;
            resources.Defer(
                $"project {project}",
                token => ownsProject ? DeleteAsync(http, $"projects/{project}", token) : Task.CompletedTask);
            resources.Defer(
                $"repository {image.Repository}",
                token => ownsProject
                    ? DeleteAsync(http, $"projects/{project}/repositories/function-sdk", token)
                    : Task.CompletedTask);
            using (var created = await http.PostAsJsonAsync(
                       "projects",
                       new { project_name = project, metadata = new { @public = "true" } },
                       cancellationToken))
            {
                if (created.StatusCode == HttpStatusCode.Conflict)
                    ownsProject = false;

                RequireSuccess(created, "create the owned public test project");
            }

            await image.BuildAsync(lab, credentials, cancellationToken);
            using var artifact = await http.GetAsync(
                $"projects/{project}/repositories/function-sdk/artifacts/{image.Tag}",
                cancellationToken);
            RequireSuccess(artifact, "verify the published Function SDK image");
            return image;
        }
        catch (Exception primaryFailure)
        {
            try
            {
                await resources.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Function SDK image creation and cleanup failed.",
                    primaryFailure,
                    cleanupFailure);
            }

            throw;
        }
    }

    private async Task BuildAsync(
        SelectedKubernetesLab lab,
        Credentials credentials,
        CancellationToken cancellationToken)
    {
        await using var buildResources = new TestResourceScope("Function SDK image build");
        await buildResources.RunAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"pole-function-sdk-{Guid.NewGuid():N}");
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(root);
            else
                Directory.CreateDirectory(
                    root,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            buildResources.Defer(
                "private build directory",
                _ =>
                {
                    Directory.Delete(root, recursive: true);
                    return Task.CompletedTask;
                });
            var context = Directory.CreateDirectory(Path.Combine(root, "context")).FullName;
            var config = Directory.CreateDirectory(Path.Combine(root, "docker-config")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(context, "FunctionFixture.csproj"),
                ProjectFile,
                cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(context, "Program.cs"), ApplicationSource, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(context, "Dockerfile"), Dockerfile, cancellationToken);
            var configFile = Path.Combine(config, "config.json");
            await File.WriteAllTextAsync(
                configFile,
                JsonSerializer.Serialize(
                    new
                    {
                        auths = new Dictionary<string, object> { [RegistryHost] = new { auth = credentials.Basic } },
                    }),
                cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(configFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var service = lab.State.Services!.BuildKit;
            var forward = await buildResources.CreateAsync(
                "BuildKit port-forward",
                token => KubernetesPortForwardSession.StartAsync(
                    lab.State.GetRequiredTarget(service.TargetKey),
                    service.Namespace,
                    $"service/{service.Service}",
                    service.Port,
                    token),
                (session, _) => session.DisposeAsync().AsTask(),
                cancellationToken);
            var start = new ProcessStartInfo(lab.State.BuildctlPath!)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[]
                     {
                         "--addr", $"tcp://127.0.0.1:{forward.LocalPort}", "build", "--progress=plain",
                         "--frontend=dockerfile.v0", "--local", $"context={context}",
                         "--local", $"dockerfile={context}", "--opt", "filename=Dockerfile",
                         "--output", $"type=image,name={Reference},push=true",
                     })
                start.ArgumentList.Add(argument);

            start.Environment["DOCKER_CONFIG"] = config;
            start.Environment["NO_COLOR"] = "1";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await RunBuildAsync(start, credentials, timeout.Token);
        });
    }

    private static async Task RunBuildAsync(
        ProcessStartInfo start,
        Credentials credentials,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Could not start the selected lab BuildKit client.");
        var sensitiveValues = new[] { credentials.Password, credentials.Basic };
        var stdout = CaptureBuildOutputAsync(process.StandardOutput, sensitiveValues);
        var stderr = CaptureBuildOutputAsync(process.StandardError, sensitiveValues);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
            throw;
        }

        var output = string.Join('\n', await stdout, await stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Function SDK image build failed ({process.ExitCode}). " + RedactBuildOutput(
                    output,
                    [credentials.Password, credentials.Basic]));
    }

    internal static async Task<string> CaptureBuildOutputAsync(
        StreamReader reader,
        IReadOnlyList<string> sensitiveValues)
    {
        const int limit = 8_192;
        var tail = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            // Redact before truncating: retaining a raw suffix could expose part of a credential.
            tail.AppendLine(RedactBuildOutput(line, sensitiveValues));
            if (tail.Length > limit)
                tail.Remove(0, tail.Length - limit);
        }

        return tail.ToString();
    }

    internal static string RedactBuildOutput(string output, IReadOnlyList<string> sensitiveValues)
    {
        foreach (var value in sensitiveValues.SelectMany(value => value.Split('\r', '\n'))
                     .Where(value => !string.IsNullOrEmpty(value))
                     .OrderByDescending(value => value.Length))
            output = output.Replace(value, "[REDACTED]", StringComparison.Ordinal);

        return output;
    }

    private static async Task<Credentials> ReadCredentialsAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("username", out var username) ||
            !document.RootElement.TryGetProperty("password", out var password) ||
            string.IsNullOrWhiteSpace(username.GetString()) || string.IsNullOrWhiteSpace(password.GetString()))
            throw new InvalidOperationException("The selected lab Harbor credentials file is malformed.");

        return new Credentials(username.GetString()!, password.GetString()!);
    }

    private static async Task DeleteAsync(
        HttpClient http,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync(path, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
            RequireSuccess(response, "clean the owned Function SDK image project");
    }

    private static void RequireSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Could not {operation}: Harbor HTTP {(int)response.StatusCode}.");
    }

    private sealed record Credentials(
        string Username,
        string Password
    )
    {
        public string Basic => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
        public override string ToString() => "Harbor credentials [REDACTED]";
    }
}
