using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Harbor;

public sealed class HarborContainerRegistryClient(
    HttpClient httpClient,
    HarborOptions options) : IContainerRegistryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<ProvisionedRegistryProject>> ProvisionProjectAsync(
        RegistryProjectProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return NotConfigured<ProvisionedRegistryProject>();

        var projectResult = await EnsureProjectAsync(request.ProjectName, cancellationToken);
        if (!projectResult.TryGetValue(out var project, out var projectFailure))
            return projectFailure;

        try
        {
            var robotNames = RegistryRobotNames.ForAttempt(request.AttemptId);
            var buildResult = await CreateRobotAsync(
                project.Name,
                robotNames.Build,
                [new HarborAccess("repository", "pull"), new HarborAccess("repository", "push")],
                cancellationToken);
            if (!buildResult.TryGetValue(out var buildCredential, out var buildFailure))
                return buildFailure;

            var pullResult = await CreateRobotAsync(
                project.Name,
                robotNames.Pull,
                [new HarborAccess("repository", "pull")],
                cancellationToken);
            if (!pullResult.TryGetValue(out var pullCredential, out var pullFailure))
                return pullFailure;

            return new ProvisionedRegistryProject(
                project.Id.ToString(),
                project.Name,
                buildCredential,
                pullCredential);
        }
        catch (HttpRequestException)
        {
            return HarborFailure<ProvisionedRegistryProject>("Network request failed.");
        }
        catch (JsonException)
        {
            return HarborFailure<ProvisionedRegistryProject>("Harbor returned malformed JSON.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HarborFailure<ProvisionedRegistryProject>("Harbor request timed out.");
        }
    }

    public async Task<Result<RegistryArtifact>> GetArtifactAsync(
        string projectName,
        string repositoryName,
        string reference,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return NotConfigured<RegistryArtifact>();

        try
        {
            var path = $"api/v2.0/projects/{Encode(projectName)}/repositories/{EncodeRepositoryName(repositoryName)}/artifacts/{Encode(reference)}";
            using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Failures.NotFound("The container registry artifact was not found.", reference);
            if (!response.IsSuccessStatusCode)
                return await HarborFailureAsync<RegistryArtifact>(response, cancellationToken);

            var artifact = await response.Content.ReadFromJsonAsync<HarborArtifact>(JsonOptions, cancellationToken);
            if (artifact is null || string.IsNullOrWhiteSpace(artifact.Digest))
                return HarborFailure<RegistryArtifact>("Harbor returned an incomplete artifact response.");

            return new RegistryArtifact(repositoryName, reference, artifact.Digest, artifact.Size);
        }
        catch (HttpRequestException)
        {
            return HarborFailure<RegistryArtifact>("Network request failed.");
        }
        catch (JsonException)
        {
            return HarborFailure<RegistryArtifact>("Harbor returned malformed JSON.");
        }
    }

    public async Task<Result> DeleteProjectAsync(
        string externalProjectId,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return NotConfigured();

        try
        {
            var projectResult = await GetProjectAsync(externalProjectId, cancellationToken);
            if (!projectResult.TryGetValue(out var lookup, out var projectFailure))
                return projectFailure;
            if (lookup.Project is null)
                return Result.Success();

            var repositoriesResult = await ListRepositoriesAsync(lookup.Project.Name, cancellationToken);
            if (!repositoriesResult.TryGetValue(out var repositories, out var repositoriesFailure))
                return repositoriesFailure;
            foreach (var repository in repositories)
            {
                var repositoryName = RepositoryNameWithoutProject(lookup.Project.Name, repository.Name);
                if (string.IsNullOrWhiteSpace(repositoryName))
                    return HarborFailure("Harbor returned a repository without a name.");

                var deleteRepositoryResult = await DeleteRepositoryAsync(
                    lookup.Project.Name,
                    repositoryName,
                    cancellationToken);
                if (deleteRepositoryResult is Failure repositoryFailure)
                    return repositoryFailure;
            }

            using var response = await SendAsync(
                HttpMethod.Delete,
                $"api/v2.0/projects/{lookup.Project.Id}",
                null,
                cancellationToken);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                return Result.Success();
            return await HarborFailureAsync(response, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return HarborFailure("Network request failed.");
        }
        catch (JsonException)
        {
            return HarborFailure("Harbor returned malformed JSON.");
        }
    }

    public async Task<Result<RegistryRobotCleanupResult>> DeleteRobotCredentialsAsync(
        IReadOnlyCollection<RegistryRobotCleanupTarget> targets,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return NotConfigured();

        Failure? firstFailure = null;
        var matchedByNameCount = 0;
        foreach (var target in targets.DistinctBy(item => item.Name, StringComparer.Ordinal))
        {
            var robotIds = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(target.ExternalRobotId))
                robotIds.Add(target.ExternalRobotId);

            if (string.IsNullOrWhiteSpace(target.ExternalRobotId))
            {
                try
                {
                    var discovered = await FindRobotIdsByNameAsync(
                        target.ProjectName,
                        target.Name,
                        cancellationToken);
                    if (discovered.TryGetValue(out var discoveredIds, out var discoveryFailure))
                    {
                        robotIds.UnionWith(discoveredIds);
                        matchedByNameCount += discoveredIds.Count;
                    }
                    else
                    {
                        firstFailure ??= discoveryFailure;
                    }
                }
                catch (HttpRequestException)
                {
                    firstFailure ??= HarborFailure("Network request failed.");
                }
                catch (JsonException)
                {
                    firstFailure ??= HarborFailure("Harbor returned malformed JSON.");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    firstFailure ??= HarborFailure("Harbor request timed out.");
                }
            }

            foreach (var robotId in robotIds)
            {
                try
                {
                    var deleted = await DeleteRobotAsync(robotId, cancellationToken);
                    if (deleted is Failure failure)
                        firstFailure ??= failure;
                }
                catch (HttpRequestException)
                {
                    firstFailure ??= HarborFailure("Network request failed.");
                }
                catch (JsonException)
                {
                    firstFailure ??= HarborFailure("Harbor returned malformed JSON.");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    firstFailure ??= HarborFailure("Harbor request timed out.");
                }
            }
        }

        return firstFailure is null
            ? new RegistryRobotCleanupResult(matchedByNameCount)
            : firstFailure;
    }

    private async Task<Result<IReadOnlyList<string>>> FindRobotIdsByNameAsync(
        string projectName,
        string robotName,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var ids = new List<string>();
        for (var page = 1; ; page++)
        {
            var query = Uri.EscapeDataString($"name={robotName}");
            using var response = await SendAsync(
                HttpMethod.Get,
                $"api/v2.0/robots?q={query}&page={page}&page_size={pageSize}",
                null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return await HarborFailureAsync<IReadOnlyList<string>>(response, cancellationToken);

            var robots = await response.Content.ReadFromJsonAsync<HarborRobot[]>(JsonOptions, cancellationToken) ?? [];
            ids.AddRange(robots
                .Where(robot => RobotMatchesCleanupTarget(robot, projectName, robotName))
                .Select(robot => robot.Id.ToString()));
            if (robots.Length < pageSize)
                return ids;
        }
    }

    private async Task<Result<HarborProject>> EnsureProjectAsync(
        string projectName,
        CancellationToken cancellationToken)
    {
        var existing = await FindProjectAsync(projectName, cancellationToken);
        if (existing.TryGetValue(out var lookup, out var findFailure) && lookup.Project is not null)
            return lookup.Project;
        if (findFailure is not null)
            return findFailure;

        using var createResponse = await SendAsync(
            HttpMethod.Post,
            "api/v2.0/projects",
            new HarborProjectRequest(projectName, false),
            cancellationToken);
        if (createResponse.StatusCode != HttpStatusCode.Conflict && !createResponse.IsSuccessStatusCode)
            return await HarborFailureAsync<HarborProject>(createResponse, cancellationToken);

        var created = await FindProjectAsync(projectName, cancellationToken);
        if (!created.TryGetValue(out lookup, out findFailure))
            return findFailure;
        return lookup.Project is null
            ? HarborFailure<HarborProject>("Harbor accepted the project but did not return it afterward.")
            : lookup.Project;
    }

    private async Task<Result<HarborProjectLookup>> FindProjectAsync(
        string projectName,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"api/v2.0/projects?name={Uri.EscapeDataString(projectName)}&page=1&page_size=100",
            null,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            return await HarborFailureAsync<HarborProjectLookup>(response, cancellationToken);

        var projects = await response.Content.ReadFromJsonAsync<HarborProject[]>(JsonOptions, cancellationToken) ?? [];
        return new HarborProjectLookup(projects.FirstOrDefault(item =>
            string.Equals(item.Name, projectName, StringComparison.Ordinal)));
    }

    private async Task<Result<HarborProjectLookup>> GetProjectAsync(
        string projectNameOrId,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"api/v2.0/projects/{Encode(projectNameOrId)}",
            null,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new HarborProjectLookup(null);
        if (!response.IsSuccessStatusCode)
            return await HarborFailureAsync<HarborProjectLookup>(response, cancellationToken);

        var project = await response.Content.ReadFromJsonAsync<HarborProject>(JsonOptions, cancellationToken);
        return project is null || string.IsNullOrWhiteSpace(project.Name)
            ? HarborFailure<HarborProjectLookup>("Harbor returned an incomplete project response.")
            : new HarborProjectLookup(project);
    }

    private async Task<Result<IReadOnlyList<HarborRepository>>> ListRepositoriesAsync(
        string projectName,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var repositories = new List<HarborRepository>();
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                $"api/v2.0/projects/{Encode(projectName)}/repositories?page={page}&page_size={pageSize}",
                null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return await HarborFailureAsync<IReadOnlyList<HarborRepository>>(response, cancellationToken);

            var currentPage = await response.Content.ReadFromJsonAsync<HarborRepository[]>(JsonOptions, cancellationToken) ?? [];
            repositories.AddRange(currentPage);
            if (currentPage.Length < pageSize)
                return repositories;
        }
    }

    private async Task<Result> DeleteRepositoryAsync(
        string projectName,
        string repositoryName,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Delete,
            $"api/v2.0/projects/{Encode(projectName)}/repositories/{EncodeRepositoryName(repositoryName)}",
            null,
            cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            return Result.Success();
        return await HarborFailureAsync(response, cancellationToken);
    }

    private async Task<Result<RegistryRobotCredential>> CreateRobotAsync(
        string projectName,
        string robotName,
        IReadOnlyList<HarborAccess> access,
        CancellationToken cancellationToken)
    {
        var request = new HarborRobotRequest(
            robotName,
            "Managed by Pole.",
            "project",
            false,
            options.RobotDurationDays,
            [new HarborRobotPermissionRequest("project", projectName, access)]);
        using var response = await SendAsync(HttpMethod.Post, "api/v2.0/robots", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return await HarborFailureAsync<RegistryRobotCredential>(response, cancellationToken);

        var robot = await response.Content.ReadFromJsonAsync<HarborRobotCreated>(JsonOptions, cancellationToken);
        if (robot is null || robot.Id <= 0 || string.IsNullOrWhiteSpace(robot.Name) || string.IsNullOrWhiteSpace(robot.Secret))
            return HarborFailure<RegistryRobotCredential>("Harbor returned an incomplete robot credential.");

        return new RegistryRobotCredential(
            robot.Id.ToString(),
            robot.Name,
            robot.Secret,
            robot.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(robot.ExpiresAt).UtcDateTime : null);
    }

    private async Task<Result> DeleteRobotAsync(string robotId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Delete,
            $"api/v2.0/robots/{Encode(robotId)}",
            null,
            cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            return Result.Success();
        return await HarborFailureAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(options.BaseUrl.TrimEnd('/') + "/"), path));
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static string Encode(string segment) => Uri.EscapeDataString(segment);

    private static bool RobotMatchesCleanupTarget(
        HarborRobot robot,
        string projectName,
        string requestedName) =>
        robot.Name?.EndsWith($"+{requestedName}", StringComparison.Ordinal) is true &&
        robot.Permissions?.Any(permission =>
            string.Equals(permission.Namespace, projectName, StringComparison.Ordinal)) is true;

    // Harbor treats nested repository names as one route parameter. Uri construction
    // unescapes the first encoding before transport, so the embedded slash must be encoded twice.
    private static string EncodeRepositoryName(string repositoryName) => Encode(Encode(repositoryName));

    private static string RepositoryNameWithoutProject(string projectName, string repositoryName)
    {
        var prefix = $"{projectName.TrimEnd('/')}/";
        return repositoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? repositoryName[prefix.Length..]
            : repositoryName;
    }

    private static Failure NotConfigured() =>
        Failures.ExternalService("The container registry is not configured.", "Harbor", "Harbor configuration is disabled or incomplete.");

    private static Failure<T> NotConfigured<T>() where T : notnull => new(NotConfigured());

    private static Failure HarborFailure(string details) =>
        Failures.ExternalService("Harbor could not complete the registry request.", "Harbor", details);

    private static Failure<T> HarborFailure<T>(string details) where T : notnull => new(HarborFailure(details));

    private static Task<Failure> HarborFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        Task.FromResult<Failure>(HarborFailure($"Status {(int)response.StatusCode}."));

    private static async Task<Failure<T>> HarborFailureAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken) where T : notnull =>
        new(await HarborFailureAsync(response, cancellationToken));

    private sealed record HarborProject(
        [property: JsonPropertyName("project_id")] long Id,
        string Name);

    private sealed record HarborProjectLookup(HarborProject? Project);

    private sealed record HarborProjectRequest(
        [property: JsonPropertyName("project_name")] string ProjectName,
        [property: JsonPropertyName("public")] bool IsPublic);

    private sealed record HarborRobotRequest(
        string Name,
        string Description,
        string Level,
        bool Disable,
        long Duration,
        IReadOnlyList<HarborRobotPermissionRequest> Permissions);

    private sealed record HarborRobotPermissionRequest(
        string Kind,
        string Namespace,
        IReadOnlyList<HarborAccess> Access);

    private sealed record HarborAccess(string Resource, string Action);

    private sealed record HarborRobotCreated(
        long Id,
        string Name,
        string Secret,
        [property: JsonPropertyName("expires_at")] long ExpiresAt)
    {
        public override string ToString() => "HarborRobotCreated([REDACTED])";
    }

    private sealed record HarborRobot(
        long Id,
        string? Name,
        HarborRobotPermissionResponse[]? Permissions);

    private sealed record HarborRobotPermissionResponse(string? Namespace);

    private sealed record HarborArtifact(string Digest, long Size);

    private sealed record HarborRepository(string Name);
}
