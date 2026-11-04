using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Contracts;

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

    public async Task<ProvisionRegistryProjectOutcome> ProvisionProjectAsync(
        RegistryProjectProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return NotConfigured();

        try
        {
            var projectResult = await EnsureProjectAsync(request.ProjectName, cancellationToken);
            if (projectResult.Value is ContainerRegistryProviderError projectError)
                return projectError;
            var project = (HarborProject)projectResult.Value;
            var robotNames = RegistryRobotNames.ForAttempt(request.AttemptId);
            var buildResult = await CreateRobotAsync(project.Name, robotNames.Build,
                [new HarborAccess("repository", "pull"), new HarborAccess("repository", "push")], cancellationToken);
            if (buildResult.Value is ContainerRegistryProviderError buildFailure)
                return buildFailure;
            var buildCredential = (RegistryRobotCredential)buildResult.Value;

            var pullResult = await CreateRobotAsync(project.Name, robotNames.Pull,
                [new HarborAccess("repository", "pull")], cancellationToken);
            if (pullResult.Value is ContainerRegistryProviderError pullFailure)
                return pullFailure;
            var pullCredential = (RegistryRobotCredential)pullResult.Value;

            return new ProvisionedRegistryProject(
                project.Id.ToString(), project.Name, buildCredential, pullCredential);
        }
        catch (HttpRequestException)
        {
            return Unavailable();
        }
        catch (JsonException)
        {
            return Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable();
        }
    }

    public async Task<GetRegistryArtifactOutcome> GetArtifactAsync(
        GetRegistryArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return new ContainerRegistryArtifactLookupError(NotConfigured());

        try
        {
            var path =
                $"api/v2.0/projects/{Encode(request.ProjectName)}/repositories/{EncodeRepositoryName(request.RepositoryName)}/artifacts/{Encode(request.Reference)}";
            using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new ContainerRegistryArtifactLookupError(new ContainerRegistryArtifactNotFound(
                    request.Reference));
            if (!response.IsSuccessStatusCode)
                return new ContainerRegistryArtifactLookupError(Unavailable());

            var artifact = await response.Content.ReadFromJsonAsync<HarborArtifact>(JsonOptions, cancellationToken);
            return artifact is null || string.IsNullOrWhiteSpace(artifact.Digest)
                ? new ContainerRegistryArtifactLookupError(Unavailable())
                : new RegistryArtifact(request.RepositoryName, request.Reference, artifact.Digest, artifact.Size);
        }
        catch (HttpRequestException)
        {
            return new ContainerRegistryArtifactLookupError(Unavailable());
        }
        catch (JsonException)
        {
            return new ContainerRegistryArtifactLookupError(Unavailable());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ContainerRegistryArtifactLookupError(Unavailable());
        }
    }

    public async Task<DeleteRegistryProjectOutcome> DeleteProjectAsync(
        string externalProjectId,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return NotConfigured();

        try
        {
            var projectResult = await GetProjectAsync(externalProjectId, cancellationToken);
            if (projectResult.Value is ContainerRegistryProviderError projectFailure)
                return projectFailure;
            var lookup = (HarborProjectLookup)projectResult.Value;
            if (lookup.Project is null)
                return new RegistryProjectDeleted();

            var repositoriesResult = await ListRepositoriesAsync(lookup.Project.Name, cancellationToken);
            if (repositoriesResult.Value is ContainerRegistryProviderError repositoriesFailure)
                return repositoriesFailure;
            var repositories = (IReadOnlyList<HarborRepository>)repositoriesResult.Value;
            foreach (var repository in repositories)
            {
                var repositoryName = RepositoryNameWithoutProject(lookup.Project.Name, repository.Name);
                if (string.IsNullOrWhiteSpace(repositoryName))
                    return Unavailable();

                var deleted = await DeleteRepositoryAsync(lookup.Project.Name, repositoryName, cancellationToken);
                if (deleted.Value is ContainerRegistryProviderError deleteFailure)
                    return deleteFailure;
            }

            using var response = await SendAsync(HttpMethod.Delete, $"api/v2.0/projects/{lookup.Project.Id}", null, cancellationToken);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound
                ? new RegistryProjectDeleted()
                : Unavailable();
        }
        catch (HttpRequestException)
        {
            return Unavailable();
        }
        catch (JsonException)
        {
            return Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable();
        }
    }

    public async Task<DeleteRegistryRobotCredentialsOutcome> DeleteRobotCredentialsAsync(
        IReadOnlyCollection<RegistryRobotCleanupTarget> targets,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return NotConfigured();

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
                    switch (discovered.Value)
                    {
                        case IReadOnlyList<string> success:
                            robotIds.UnionWith(success);
                            matchedByNameCount += success.Count;
                            break;
                        case ContainerRegistryProviderError error:
                            firstFailure ??= error;
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
                    if (deleted.Value is ContainerRegistryProviderError error)
                        firstFailure ??= error;
                }
                catch (HttpRequestException) { firstFailure ??= Unavailable(); }
                catch (JsonException) { firstFailure ??= Unavailable(); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { firstFailure ??= Unavailable(); }
            }
        }

        if (firstFailure is not null)
            return firstFailure.Value;

        return new RegistryRobotCleanupResult(matchedByNameCount);
    }

    private async Task<FindHarborRobotIdsOutcome> FindRobotIdsByNameAsync(
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
                return Unavailable();

            var robots = await response.Content.ReadFromJsonAsync<HarborRobot[]>(JsonOptions, cancellationToken) ?? [];
            ids.AddRange(robots.Where(robot => RobotMatchesCleanupTarget(robot, projectName, robotName)).Select(robot => robot.Id.ToString()));
            if (robots.Length < pageSize)
                return (IReadOnlyList<string>)ids;
        }
    }

    private async Task<EnsureHarborProjectOutcome> EnsureProjectAsync(
        string projectName,
        CancellationToken cancellationToken)
    {
        var existing = await FindProjectAsync(projectName, cancellationToken);
        if (existing.Value is ContainerRegistryProviderError existingFailure)
            return existingFailure;
        var lookup = (HarborProjectLookup)existing.Value;
        if (lookup.Project is not null)
            return lookup.Project;

        using var createResponse = await SendAsync(HttpMethod.Post, "api/v2.0/projects", new HarborProjectRequest(projectName, false), cancellationToken);
        if (createResponse.StatusCode != HttpStatusCode.Conflict && !createResponse.IsSuccessStatusCode)
            return Unavailable();

        var created = await FindProjectAsync(projectName, cancellationToken);
        if (created.Value is ContainerRegistryProviderError createdFailure)
            return createdFailure;
        lookup = (HarborProjectLookup)created.Value;
        return lookup.Project is null
            ? Unavailable()
            : lookup.Project;
    }

    private async Task<LookupHarborProjectOutcome> FindProjectAsync(
        string projectName,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"api/v2.0/projects?name={Uri.EscapeDataString(projectName)}&page=1&page_size=100", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return Unavailable();

        var projects = await response.Content.ReadFromJsonAsync<HarborProject[]>(JsonOptions, cancellationToken) ?? [];
        return new HarborProjectLookup(projects.FirstOrDefault(item =>
            string.Equals(item.Name, projectName, StringComparison.Ordinal)));
    }

    private async Task<LookupHarborProjectOutcome> GetProjectAsync(
        string projectNameOrId,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v2.0/projects/{Encode(projectNameOrId)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new HarborProjectLookup(null);
        if (!response.IsSuccessStatusCode)
            return Unavailable();

        var project = await response.Content.ReadFromJsonAsync<HarborProject>(JsonOptions, cancellationToken);
        return project is null || string.IsNullOrWhiteSpace(project.Name)
            ? Unavailable()
            : new HarborProjectLookup(project);
    }

    private async Task<ListHarborRepositoriesOutcome> ListRepositoriesAsync(
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
                return Unavailable();

            var currentPage = await response.Content.ReadFromJsonAsync<HarborRepository[]>(JsonOptions, cancellationToken) ?? [];
            repositories.AddRange(currentPage);
            if (currentPage.Length < pageSize)
                return (IReadOnlyList<HarborRepository>)repositories;
        }
    }

    private async Task<DeleteHarborRepositoryOutcome> DeleteRepositoryAsync(
        string projectName,
        string repositoryName,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete,
            $"api/v2.0/projects/{Encode(projectName)}/repositories/{EncodeRepositoryName(repositoryName)}", null, cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound
            ? new HarborRepositoryDeleted()
            : Unavailable();
    }

    private async Task<CreateHarborRobotOutcome> CreateRobotAsync(
        string projectName,
        string robotName,
        IReadOnlyList<HarborAccess> access,
        CancellationToken cancellationToken)
    {
        var request = new HarborRobotRequest(robotName, "Managed by Pole.", "project", false, options.RobotDurationDays,
            [new HarborRobotPermissionRequest("project", projectName, access)]);
        using var response = await SendAsync(HttpMethod.Post, "api/v2.0/robots", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return Unavailable();

        var robot = await response.Content.ReadFromJsonAsync<HarborRobotCreated>(JsonOptions, cancellationToken);
        if (robot is null || robot.Id <= 0 || string.IsNullOrWhiteSpace(robot.Name) || string.IsNullOrWhiteSpace(robot.Secret))
            return Unavailable();

        return new RegistryRobotCredential(robot.Id.ToString(), robot.Name, robot.Secret,
            robot.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(robot.ExpiresAt).UtcDateTime : null);
    }

    private async Task<DeleteHarborRobotOutcome> DeleteRobotAsync(string robotId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"api/v2.0/robots/{Encode(robotId)}", null, cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound
            ? new HarborRobotDeleted()
            : Unavailable();
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

    private union FindHarborRobotIdsOutcome(
        IReadOnlyList<string>,
        ContainerRegistryProviderError);

    private union EnsureHarborProjectOutcome(
        HarborProject,
        ContainerRegistryProviderError);

    private union LookupHarborProjectOutcome(
        HarborProjectLookup,
        ContainerRegistryProviderError);

    private union ListHarborRepositoriesOutcome(
        IReadOnlyList<HarborRepository>,
        ContainerRegistryProviderError);

    private sealed record HarborRepositoryDeleted;

    private union DeleteHarborRepositoryOutcome(
        HarborRepositoryDeleted,
        ContainerRegistryProviderError);

    private union CreateHarborRobotOutcome(
        RegistryRobotCredential,
        ContainerRegistryProviderError);

    private sealed record HarborRobotDeleted;

    private union DeleteHarborRobotOutcome(
        HarborRobotDeleted,
        ContainerRegistryProviderError);

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
