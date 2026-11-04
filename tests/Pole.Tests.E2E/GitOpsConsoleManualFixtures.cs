using System.Net;
using System.Text.Json;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E;

public sealed partial class GitOpsRepositoryFlowE2ETests
{
    private sealed record ConsoleManualIdentities(
        string Group,
        string Web,
        string Secret,
        string Binding,
        long SecretVersion);

    private async Task<ConsoleManualIdentities> CreateConsoleManualInventoryAsync(string profile, string value,
        CancellationToken ct)
    {
        var group = await JsonAsync(HttpMethod.Post, "/api/v1/resource-groups", new
        {
            name = ConsoleAdoptedGroup,
            description = "Manual console adoption",
            tags = new[] { "e2e" }
        }, ct);
        var groupId = group.GetProperty("id").GetString()!;
        var profiles = await AdminJsonAsync(HttpMethod.Get, "/api/v1/platform/compute-profiles?pageSize=200", null, ct);
        var compute = profiles.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("technicalName").GetString() == profile);
        var targets = await AdminJsonAsync(HttpMethod.Get, "/api/v1/platform/deployment-targets", null, ct);
        var target = targets.EnumerateArray().Single(item => item.GetProperty("isDefault").GetBoolean());
        var probe = new
        {
            path = "/",
            port = "80",
            initialDelaySeconds = 0,
            periodSeconds = 2,
            timeoutSeconds = 1,
            failureThreshold = 3,
            successThreshold = 1
        };
        var web = await JsonAsync(HttpMethod.Post, "/api/v1/web-services", new
        {
            name = ConsoleAdoptedWeb,
            resourceGroupId = groupId,
            exposure = "Private",
            tags = new[] { "e2e" },
            spec = new
            {
                image = new { server = "docker.io", name = "traefik/whoami", tag = "v1.11.0" },
                config = new
                {
                    environmentVariables = new Dictionary<string, string>(),
                    portMappings = new[]
                    {
                        new { name = "http", servicePort = 80, targetPort = "80", protocol = "TCP" }
                    }
                },
                autoscaling = (object?)null,
                probes = new { startup = probe, liveness = probe, readiness = probe }
            },
            placement = new
            {
                isolationScopeId = group.GetProperty("defaultIsolationScopeId").GetString(),
                targets = new[] { new { deploymentTargetId = target.GetProperty("id").GetString(), fixedReplicas = 1,
                    computeProfileId = compute.GetProperty("id").GetString() } }
            }
        }, ct);
        var webId = web.GetProperty("webServiceId").GetString()!;
        var secret = await JsonAsync(HttpMethod.Post, "/api/v1/secrets", new
        {
            resourceGroupId = groupId,
            isolationScopeId = group.GetProperty("defaultIsolationScopeId").GetString(),
            name = ConsoleAdoptedSecret,
            purpose = "Manual console adoption",
            values = new Dictionary<string, string> { ["password"] = value }
        }, ct);
        var secretId = secret.GetProperty("id").GetString()!;
        await JsonAsync(HttpMethod.Post, $"/api/v1/secrets/{secretId}/bindings", new
        {
            consumerType = "pole/web-service",
            consumerId = webId,
            allDeployments = true,
            deploymentIds = Array.Empty<string>()
        }, ct);
        var usage = await JsonAsync(HttpMethod.Get, $"/api/v1/secrets/{secretId}/usage", null, ct);
        var binding = Assert.Single(usage.EnumerateArray()).GetProperty("binding");
        return new(groupId, webId, secretId, binding.GetProperty("id").GetString()!,
            secret.GetProperty("currentVersion").GetInt64());
    }

    private static IReadOnlyList<RepositoryExampleFile> ConsoleAdoptionFiles(string target, string profile)
    {
        var files = RepositoryExampleFiles.Read("src/features/GitOps/examples/minimal/manifests", "console")
            .Concat(RepositoryExampleFiles.Read("src/features/GitOps/examples/full/manifests", "console")
                .Where(file => file.Path.EndsWith("/secret.yaml", StringComparison.Ordinal) ||
                    file.Path.EndsWith("/secret-binding-web.yaml", StringComparison.Ordinal)));
        return files.Select(file => file with
        {
            Content = file.Content
                .Replace("gitops-minimal-group", ConsoleAdoptedGroup, StringComparison.Ordinal)
                .Replace("gitops-mixed-group", ConsoleAdoptedGroup, StringComparison.Ordinal)
                .Replace("gitops-mixed-auth", ConsoleAdoptedSecret, StringComparison.Ordinal)
                .Replace("mixed-web-binding", ConsoleAdoptedBinding, StringComparison.Ordinal)
                .Replace("minimal-web", ConsoleAdoptedWeb, StringComparison.Ordinal)
                .Replace("mixed-web", ConsoleAdoptedWeb, StringComparison.Ordinal)
                .Replace("  isolationScope: gitops-mixed-scope\n", "", StringComparison.Ordinal)
                .Replace("demo-target", target, StringComparison.Ordinal)
                .Replace("demo-compute", profile, StringComparison.Ordinal)
        }).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }
}
