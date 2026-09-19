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

/// <summary>
/// The Harbor provider protocol exposes sanitized typed outcomes. Transport and
/// cancellation failures still propagate to the standard infrastructure path.
/// </summary>
public sealed class HarborContainerRegistryClient(
    HttpClient httpClient,
    HarborOptions options) : IContainerRegistryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<ProvisionedRegistryProject, ContainerRegistryProviderError>> ProvisionProjectAsync(
        RegistryProjectProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return new Err<ContainerRegistryProviderError>(NotConfigured());

        try
        {
            var projectResult = await EnsureProjectAsync(request.ProjectName, cancellationToken);
            var ensuredProject = TypedResultExtensions.TryGetValue(projectResult);
            if (!ensuredProject.Succeeded)
                return new Err<ContainerRegistryProviderError>(ensuredProject.Error!);
            var project = ensuredProject.Value!;
            var robotNames = RegistryRobotNames.ForAttempt(request.AttemptId);
            var buildResult = await CreateRobotAsync(project.Name, robotNames.Build,
                [new HarborAccess("repository", "pull"), new HarborAccess("repository", "push")], cancellationToken);
            if (buildResult is Err<ContainerRegistryProviderError> buildFailure)
                return new Err<ContainerRegistryProviderError>(buildFailure.Value);
            var buildCredential = TypedResultExtensions.TryGetValue(buildResult).Value!;

            var pullResult = await CreateRobotAsync(project.Name, robotNames.Pull,
                [new HarborAccess("repository", "pull")], cancellationToken);
            if (pullResult is Err<ContainerRegistryProviderError> pullFailure)
                return new Err<ContainerRegistryProviderError>(pullFailure.Value);
            var pullCredential = TypedResultExtensions.TryGetValue(pullResult).Value!;

            return new Ok<ProvisionedRegistryProject>(new(
                project.Id.ToString(), project.Name, buildCredential, pullCredential));
        }
        catch (HttpRequestException)
        {
            return new Err<ContainerRegistryProviderError>(Unavailable());
        }
        catch (JsonException)
        {
            return new Err<ContainerRegistryProviderError>(Unavailable());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Err<ContainerRegistryProviderError>(Unavailable());
        }
    }

    public async Task<Result<RegistryArtifact, ContainerRegistryArtifactLookupError>> GetArtifactAsync(
        string projectName,
        string repositoryName,
        string reference,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return new Err<ContainerRegistryArtifactLookupError>(new(NotConfigured()));

        try
        {
            var path = $"api/v2.0/projects/{Encode(projectName)}/repositories/{EncodeRepositoryName(repositoryName)}/artifacts/{Encode(reference)}";
            using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new Err<ContainerRegistryArtifactLookupError>(new(new ContainerRegistryArtifactNotFound(reference)));
            if (!response.IsSuccessStatusCode)
                return new Err<ContainerRegistryArtifactLookupError>(new(Unavailable()));

            var artifact = await response.Content.ReadFromJsonAsync<HarborArtifact>(JsonOptions, cancellationToken);
            return artifact is null || string.IsNullOrWhiteSpace(artifact.Digest)
                ? new Err<ContainerRegistryArtifactLookupError>(new(Unavailable()))
                : new Ok<RegistryArtifact>(new(repositoryName, reference, artifact.Digest, artifact.Size));
        }
        catch (HttpRequestException)
        {
            return new Err<ContainerRegistryArtifactLookupError>(new(Unavailable()));
        }
        catch (JsonException)
        {
            return new Err<ContainerRegistryArtifactLookupError>(new(Unavailable()));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Err<ContainerRegistryArtifactLookupError>(new(Unavailable()));
        }
    }

    public async Task<Result<Unit, ContainerRegistryProviderError>> DeleteProjectAsync(
        string externalProjectId,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return new Err<ContainerRegistryProviderError>(NotConfigured());

        try
        {
            var projectResult = await GetProjectAsync(externalProjectId, cancellationToken);
            if (projectResult is Err<ContainerRegistryProviderError> projectFailure)
                return new Err<ContainerRegistryProviderError>(projectFailure.Value);
            var lookup = TypedResultExtensions.TryGetValue(projectResult).Value!;
            if (lookup.Project is null)
                return new Ok<Unit>(Unit.Value);

            var repositoriesResult = await ListRepositoriesAsync(lookup.Project.Name, cancellationToken);
            if (repositoriesResult is Err<ContainerRegistryProviderError> repositoriesFailure)
                return new Err<ContainerRegistryProviderError>(repositoriesFailure.Value);
            var repositories = TypedResultExtensions.TryGetValue(repositoriesResult).Value!;
            foreach (var repository in repositories)
            {
                var repositoryName = RepositoryNameWithoutProject(lookup.Project.Name, repository.Name);
                if (string.IsNullOrWhiteSpace(repositoryName))
                    return new Err<ContainerRegistryProviderError>(Unavailable());

                var deleted = await DeleteRepositoryAsync(lookup.Project.Name, repositoryName, cancellationToken);
                if (deleted is Err<ContainerRegistryProviderError> deleteFailure)
                    return new Err<ContainerRegistryProviderError>(deleteFailure.Value);
            }

            using var response = await SendAsync(HttpMethod.Delete, $"api/v2.0/projects/{lookup.Project.Id}", null, cancellationToken);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound
                ? new Ok<Unit>(Unit.Value)
                : new Err<ContainerRegistryProviderError>(Unavailable());
        }
        catch (HttpRequestException)
        {
            return new Err<ContainerRegistryProviderError>(Unavailable());
        }
        catch (JsonException)
        {
            return new Err<ContainerRegistryProviderError>(Unavailable());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Err<ContainerRegistryProviderError>(Unavailable());
        }
    }

    public async Task<Result<RegistryRobotCleanupResult, ContainerRegistryProviderError>> DeleteRobotCredentialsAsync(
        IReadOnlyCollection<RegistryRobotCleanupTarget> targets,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return new Err<ContainerRegistryProviderError>(NotConfigured());

        ContainerRegistryProviderError? firstFailure = null;
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
                    var discovered = await FindRobotIdsByNameAsync(target.ProjectName, target.Name, cancellationToken);
                    switch (discovered)
                    {
                        case Ok<IReadOnlyList<string>> success:
                            robotIds.UnionWith(success.Value);
                            matchedByNameCount += success.Value.Count;
                            break;
                        case Err<ContainerRegistryProviderError> error:
                            firstFailure ??= error.Value;
                            break;
                    }
                }
                catch (HttpRequestException) { firstFailure ??= Unavailable(); }
                catch (JsonException) { firstFailure ??= Unavailable(); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { firstFailure ??= Unavailable(); }
            }

            foreach (var robotId in robotIds)
            {
                try
                {
                    var deleted = await DeleteRobotAsync(robotId, cancellationToken);
                    if (deleted is Err<ContainerRegistryProviderError> error)
                        firstFailure ??= error.Value;
                }
                catch (HttpRequestException) { firstFailure ??= Unavailable(); }
                catch (JsonException) { firstFailure ??= Unavailable(); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { firstFailure ??= Unavailable(); }
            }
        }

        if (firstFailure is not null)
            return new Err<ContainerRegistryProviderError>(firstFailure.Value);

        return new Ok<RegistryRobotCleanupResult>(new(matchedByNameCount));
    }

    private async Task<Result<IReadOnlyList<string>, ContainerRegistryProviderError>> FindRobotIdsByNameAsync(
        string projectName,
        string robotName,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var ids = new List<string>();
        for (var page = 1; ; page++)
        {
            var query = Uri.EscapeDataString($"name={robotName}");
            using var response = await SendAsync(HttpMethod.Get,
                $"api/v2.0/robots?q={query}&page={page}&page_size={pageSize}", null, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new Err<ContainerRegistryProviderError>(Unavailable());

            var robots = await response.Content.ReadFromJsonAsync<HarborRobot[]>(JsonOptions, cancellationToken) ?? [];
            ids.AddRange(robots.Where(robot => RobotMatchesCleanupTarget(robot, projectName, robotName)).Select(robot => robot.Id.ToString()));
            if (robots.Length < pageSize)
                return new Ok<IReadOnlyList<string>>(ids);
        }
    }

    private async Task<Result<HarborProject, ContainerRegistryProviderError>> EnsureProjectAsync(
        string projectName,
        CancellationToken cancellationToken)
    {
        var existing = await FindProjectAsync(projectName, cancellationToken);
        if (existing is Err<ContainerRegistryProviderError> existingFailure)
            return new Err<ContainerRegistryProviderError>(existingFailure.Value);
        var lookup = TypedResultExtensions.TryGetValue(existing).Value!;
        if (lookup.Project is not null)
            return new Ok<HarborProject>(lookup.Project);

        using var createResponse = await SendAsync(HttpMethod.Post, "api/v2.0/projects", new HarborProjectRequest(projectName, false), cancellationToken);
        if (createResponse.StatusCode != HttpStatusCode.Conflict && !createResponse.IsSuccessStatusCode)
            return new Err<ContainerRegistryProviderError>(Unavailable());

        var created = await FindProjectAsync(projectName, cancellationToken);
        if (created is Err<ContainerRegistryProviderError> createdFailure)
            return new Err<ContainerRegistryProviderError>(createdFailure.Value);
        lookup = TypedResultExtensions.TryGetValue(created).Value!;
        return lookup.Project is null
            ? new Err<ContainerRegistryProviderError>(Unavailable())
            : new Ok<HarborProject>(lookup.Project);
    }

    private async Task<Result<HarborProjectLookup, ContainerRegistryProviderError>> FindProjectAsync(
        string projectName,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"api/v2.0/projects?name={Uri.EscapeDataString(projectName)}&page=1&page_size=100", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new Err<ContainerRegistryProviderError>(Unavailable());

        var projects = await response.Content.ReadFromJsonAsync<HarborProject[]>(JsonOptions, cancellationToken) ?? [];
        return new Ok<HarborProjectLookup>(new(projects.FirstOrDefault(item =>
            string.Equals(item.Name, projectName, StringComparison.Ordinal))));
    }

    private async Task<Result<HarborProjectLookup, ContainerRegistryProviderError>> GetProjectAsync(
        string projectNameOrId,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v2.0/projects/{Encode(projectNameOrId)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new Ok<HarborProjectLookup>(new(null));
        if (!response.IsSuccessStatusCode)
            return new Err<ContainerRegistryProviderError>(Unavailable());

        var project = await response.Content.ReadFromJsonAsync<HarborProject>(JsonOptions, cancellationToken);
        return project is null || string.IsNullOrWhiteSpace(project.Name)
            ? new Err<ContainerRegistryProviderError>(Unavailable())
            : new Ok<HarborProjectLookup>(new(project));
    }

    private async Task<Result<IReadOnlyList<HarborRepository>, ContainerRegistryProviderError>> ListRepositoriesAsync(
        string projectName,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var repositories = new List<HarborRepository>();
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(HttpMethod.Get,
                $"api/v2.0/projects/{Encode(projectName)}/repositories?page={page}&page_size={pageSize}", null, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new Err<ContainerRegistryProviderError>(Unavailable());

            var currentPage = await response.Content.ReadFromJsonAsync<HarborRepository[]>(JsonOptions, cancellationToken) ?? [];
            repositories.AddRange(currentPage);
            if (currentPage.Length < pageSize)
                return new Ok<IReadOnlyList<HarborRepository>>(repositories);
        }
    }

    private async Task<Result<Unit, ContainerRegistryProviderError>> DeleteRepositoryAsync(
        string projectName,
        string repositoryName,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete,
            $"api/v2.0/projects/{Encode(projectName)}/repositories/{EncodeRepositoryName(repositoryName)}", null, cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound
            ? new Ok<Unit>(Unit.Value)
            : new Err<ContainerRegistryProviderError>(Unavailable());
    }

    private async Task<Result<RegistryRobotCredential, ContainerRegistryProviderError>> CreateRobotAsync(
        string projectName,
        string robotName,
        IReadOnlyList<HarborAccess> access,
        CancellationToken cancellationToken)
    {
        var request = new HarborRobotRequest(robotName, "Managed by Pole.", "project", false, options.RobotDurationDays,
            [new HarborRobotPermissionRequest("project", projectName, access)]);
        using var response = await SendAsync(HttpMethod.Post, "api/v2.0/robots", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new Err<ContainerRegistryProviderError>(Unavailable());

        var robot = await response.Content.ReadFromJsonAsync<HarborRobotCreated>(JsonOptions, cancellationToken);
        if (robot is null || robot.Id <= 0 || string.IsNullOrWhiteSpace(robot.Name) || string.IsNullOrWhiteSpace(robot.Secret))
            return new Err<ContainerRegistryProviderError>(Unavailable());

        return new Ok<RegistryRobotCredential>(new(robot.Id.ToString(), robot.Name, robot.Secret,
            robot.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(robot.ExpiresAt).UtcDateTime : null));
    }

    private async Task<Result<Unit, ContainerRegistryProviderError>> DeleteRobotAsync(string robotId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"api/v2.0/robots/{Encode(robotId)}", null, cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound
            ? new Ok<Unit>(Unit.Value)
            : new Err<ContainerRegistryProviderError>(Unavailable());
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(options.BaseUrl.TrimEnd('/') + "/"), path));
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static ContainerRegistryProviderError NotConfigured() => new(new ContainerRegistryProviderNotConfigured("Harbor"));
    private static ContainerRegistryProviderError Unavailable() => new(new ContainerRegistryProviderUnavailable("Harbor"));
    private static string Encode(string segment) => Uri.EscapeDataString(segment);
    private static string EncodeRepositoryName(string repositoryName) => Encode(Encode(repositoryName));

    private static bool RobotMatchesCleanupTarget(HarborRobot robot, string projectName, string requestedName) =>
        robot.Name?.EndsWith($"+{requestedName}", StringComparison.Ordinal) is true &&
        robot.Permissions?.Any(permission => string.Equals(permission.Namespace, projectName, StringComparison.Ordinal)) is true;

    private static string RepositoryNameWithoutProject(string projectName, string repositoryName)
    {
        var prefix = $"{projectName.TrimEnd('/')}/";
        return repositoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? repositoryName[prefix.Length..] : repositoryName;
    }

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
    private sealed record HarborAccess(
        string Resource,
        string Action);
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
    private sealed record HarborArtifact(
        string Digest,
        long Size);
    private sealed record HarborRepository(string Name);
}
