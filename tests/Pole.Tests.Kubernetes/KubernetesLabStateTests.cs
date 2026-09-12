using System.Text.Json;
using System.Text.Json.Nodes;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.Kubernetes;

[Collection(EnvironmentCollection.Name)]
[Trait(TestTraits.Layer, TestTraits.Layers.Integration)]
[Trait(TestTraits.Concern, "Safety")]
[Trait(TestTraits.Feature, "KubernetesLab")]
public sealed class KubernetesLabStateTests
{
    [Fact]
    public void Selected_target_ignores_ambient_kubeconfig_and_embedded_current_context()
    {
        using var environment = new LabEnvironment();
        var ambient = environment.WriteKubeconfig(
            "ambient.yaml",
            "ambient",
            ("ambient", "https://ambient.invalid"));
        var selected = environment.WriteKubeconfig(
            "pole-lab-minimal.yaml",
            currentContext: "first",
            ("first", "https://first.invalid"),
            ("k3d-pole-lab-minimal", "https://127.0.0.1:6551"));
        var state = environment.WriteState(selected, "k3d-pole-lab-minimal");
        environment.Set("KUBECONFIG", ambient);
        environment.Set(KubernetesLabSelection.StateEnvironmentVariable, state);
        environment.Set(KubernetesLabSelection.TargetEnvironmentVariable, "default");

        var target = KubernetesLabSelection.RequireSelected();
        var configuration = target.CreateConfiguration();

        Assert.Equal("https://127.0.0.1:6551", configuration.Host);
        Assert.Equal(selected, target.KubeconfigPath);
        Assert.Equal("k3d-pole-lab-minimal", target.Context);
        Assert.Equal(
            ["--kubeconfig", selected, "--context", "k3d-pole-lab-minimal"],
            target.KubectlSelectionArguments);
    }

    [Fact]
    public void Relative_state_path_is_rejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => KubernetesLabState.Load("state.json"));
        Assert.Contains("absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_selection_resolves_repository_state_and_default_target()
    {
        using var environment = new LabEnvironment();
        environment.Set(KubernetesLabSelection.StateEnvironmentVariable, null);
        environment.Set(KubernetesLabSelection.TargetEnvironmentVariable, null);

        Assert.Equal(
            Path.Combine(RepositoryPaths.FindSharedRoot(), ".tmp", "kubernetes-lab", "state.json"),
            KubernetesLabSelection.ResolveStatePath());
        Assert.Equal("default", KubernetesLabSelection.ResolveTargetKey());
    }

    [Fact]
    public void Relative_state_override_remains_invalid()
    {
        using var environment = new LabEnvironment();
        environment.Set(KubernetesLabSelection.StateEnvironmentVariable, "state.json");

        var exception = Assert.Throws<InvalidOperationException>(KubernetesLabSelection.RequireSelected);

        Assert.Contains("absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test_identity_produces_exact_labels_and_bounded_unique_namespace_names()
    {
        using var environment = new LabEnvironment();
        environment.Set(TestRunIdentity.WorktreeEnvironmentVariable, new string('a', 19));
        environment.Set(TestRunIdentity.RunEnvironmentVariable, new string('b', 19));
        var identity = TestRunIdentity.Create();

        var first = identity.CreateNamespaceName("a-very-long-purpose");
        var second = identity.CreateNamespaceName("a-very-long-purpose");

        Assert.Equal(TestRunIdentity.Owner, identity.NamespaceLabels[TestRunIdentity.OwnerLabel]);
        Assert.Equal(new string('a', 19), identity.NamespaceLabels[TestRunIdentity.WorktreeLabel]);
        Assert.Equal(new string('b', 19), identity.NamespaceLabels[TestRunIdentity.RunLabel]);
        Assert.InRange(first.Length, 1, 63);
        Assert.Matches("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Test_identity_rejects_invalid_environment_overrides()
    {
        using var environment = new LabEnvironment();
        environment.Set(TestRunIdentity.WorktreeEnvironmentVariable, "unsafe/value");

        var exception = Assert.Throws<InvalidOperationException>(TestRunIdentity.Create);

        Assert.Contains(TestRunIdentity.WorktreeEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Configured_malformed_state_is_not_hidden_as_a_skip()
    {
        using var environment = new LabEnvironment();
        var malformed = Path.Combine(environment.Root, "state.json");
        File.WriteAllText(malformed, "{}");
        environment.Set(KubernetesLabSelection.StateEnvironmentVariable, malformed);
        environment.Set(KubernetesLabSelection.TargetEnvironmentVariable, "default");

        Assert.Null(KubernetesLabSelection.GetOptInSkipReason("api"));
        Assert.Throws<InvalidOperationException>(KubernetesLabSelection.RequireSelected);
    }

    [Fact]
    public void State_only_override_uses_default_target_and_missing_state_is_not_hidden_as_a_skip()
    {
        using var environment = new LabEnvironment();
        environment.Set(KubernetesLabSelection.StateEnvironmentVariable, Path.Combine(environment.Root, "state.json"));
        environment.Set(KubernetesLabSelection.TargetEnvironmentVariable, null);

        Assert.Null(KubernetesLabSelection.GetOptInSkipReason("api"));
        Assert.Equal(KubernetesLabSelection.DefaultTarget, KubernetesLabSelection.ResolveTargetKey());
        Assert.Throws<FileNotFoundException>(KubernetesLabSelection.RequireSelected);
    }

    [Fact]
    public void Missing_capability_produces_an_explicit_skip_reason()
    {
        using var environment = new LabEnvironment();
        var selected = environment.WriteKubeconfig(
            "pole-lab-minimal.yaml",
            "k3d-pole-lab-minimal",
            ("k3d-pole-lab-minimal", "https://127.0.0.1:6551"));
        var state = environment.WriteState(selected, "k3d-pole-lab-minimal");
        environment.Set(KubernetesLabSelection.StateEnvironmentVariable, state);
        environment.Set(KubernetesLabSelection.TargetEnvironmentVariable, "default");

        var reason = KubernetesLabSelection.GetOptInSkipReason("cnpg");

        Assert.Contains("cnpg", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Tampered_profile_contract_is_rejected()
    {
        using var environment = new LabEnvironment();
        var selected = environment.WriteKubeconfig(
            "pole-lab-minimal.yaml",
            "k3d-pole-lab-minimal",
            ("k3d-pole-lab-minimal", "https://127.0.0.1:6551"));
        var statePath = environment.WriteState(selected, "k3d-pole-lab-minimal");
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        state["targets"]!.AsArray()[0]!["apiPort"] = 6552;
        File.WriteAllText(statePath, state.ToJsonString());

        var exception = Assert.Throws<InvalidOperationException>(() => KubernetesLabState.Load(statePath));

        Assert.Contains("profile contract", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Focused_profile_rejects_Harbor_and_BuildKit_capabilities()
    {
        using var environment = new LabEnvironment();
        var selected = environment.WriteKubeconfig(
            "pole-lab-minimal.yaml",
            "k3d-pole-lab-minimal",
            ("k3d-pole-lab-minimal", "https://127.0.0.1:6551"));
        var statePath = environment.WriteState(selected, "k3d-pole-lab-minimal");
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        state["overlays"] = new JsonArray("harbor");
        state["targets"]!.AsArray()[0]!["capabilities"]!.AsArray().Add("harbor");
        File.WriteAllText(statePath, state.ToJsonString());

        var exception = Assert.Throws<InvalidOperationException>(() => KubernetesLabState.Load(statePath));

        Assert.Contains("Focused", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Full_profile_exposes_target_scoped_capabilities_and_shared_service_contracts()
    {
        using var environment = new LabEnvironment();
        var statePath = environment.WriteFullState();

        var state = KubernetesLabState.Load(statePath);

        Assert.Equal(new string('a', 32), state.InstanceId);
        Assert.Equal(new string('b', 64), state.Registry.ContainerId);
        Assert.Equal("full", state.Profile);
        Assert.Equal("docker", state.Runtime.Name);
        Assert.False(state.Runtime.TestcontainersRyukDisabled);
        Assert.True(state.GetRequiredTarget("default").HasCapability("topology"));
        Assert.True(state.GetRequiredTarget("default").HasCapability("harbor"));
        Assert.True(state.GetRequiredTarget("workload-a").HasCapability("monitoring"));
        Assert.False(state.GetRequiredTarget("workload-a").HasCapability("harbor"));
        Assert.Equal("harbor.pole.localhost:5002", state.Services!.Harbor.RegistryHost);
        Assert.Equal("buildkit", state.Services.BuildKit.Service);
        Assert.Equal(Path.Combine(environment.Root, "bin", "buildctl"), state.BuildctlPath);
    }

    [Fact]
    public void Podman_runtime_bootstrap_configures_Testcontainers_and_Aspire_without_overwriting_explicit_values()
    {
        using var environment = new LabEnvironment();
        environment.Set("DOCKER_HOST", null);
        environment.Set("DOCKER_SOCK", null);
        environment.Set("TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE", "/explicit/socket.sock");
        environment.Set("TESTCONTAINERS_RYUK_DISABLED", null);
        environment.Set("ASPIRE_CONTAINER_RUNTIME", null);

        ContainerRuntimeBootstrap.Configure(new KubernetesLabRuntime(
            "podman",
            true,
            "unix:///run/user/1000/podman/podman.sock",
            "/run/user/1000/podman/podman.sock",
            true));

        Assert.Equal("unix:///run/user/1000/podman/podman.sock", Environment.GetEnvironmentVariable("DOCKER_HOST"));
        Assert.Equal("/run/user/1000/podman/podman.sock", Environment.GetEnvironmentVariable("DOCKER_SOCK"));
        Assert.Equal("/explicit/socket.sock", Environment.GetEnvironmentVariable("TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE"));
        Assert.Equal("true", Environment.GetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED"));
        Assert.Equal("podman", Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME"));
    }

    [Fact]
    public void Docker_runtime_bootstrap_selects_Docker_without_disabling_Ryuk()
    {
        using var environment = new LabEnvironment();
        environment.Set("ASPIRE_CONTAINER_RUNTIME", null);
        environment.Set("TESTCONTAINERS_RYUK_DISABLED", null);

        ContainerRuntimeBootstrap.Configure(new KubernetesLabRuntime(
            "docker",
            false,
            null,
            null,
            false));

        Assert.Equal("docker", Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME"));
        Assert.Null(Environment.GetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED"));
    }

    [Fact]
    public void Full_profile_rejects_a_self_consistent_but_incomplete_overlay_contract()
    {
        using var environment = new LabEnvironment();
        var statePath = environment.WriteFullState();
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        state["overlays"] = new JsonArray();
        state["targets"]!.AsArray()[0]!["capabilities"] = new JsonArray(
            "api",
            "dns",
            "ingress",
            "storage",
            "registry",
            "multi-cluster",
            "topology");
        File.WriteAllText(statePath, state.ToJsonString());

        var exception = Assert.Throws<InvalidOperationException>(() => KubernetesLabState.Load(statePath));

        Assert.Contains("exact checked-in overlay contract", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Kubernetes_diagnostics_redact_common_credentials()
    {
        const string diagnostic =
            "Authorization: Bearer abcdef password=hunter2 token=abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKL " +
            "url=https://user:super-secret@example.test eyJabcdefghijklmnop.qrstuvwxyz012345 " +
            "{\"password\":\"json-secret\"}";

        var sanitized = KubernetesIntegrationTestBase.SanitizeDiagnostic(diagnostic);

        Assert.DoesNotContain("hunter2", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKL", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJabcdefghijklmnop", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("json-secret", sanitized, StringComparison.Ordinal);
    }

    private sealed class LabEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);

        public LabEnvironment()
        {
            Root = Path.Combine(Path.GetTempPath(), "pole-kubernetes-lab-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "bin"));
            Directory.CreateDirectory(Path.Combine(Root, "kubeconfigs"));
            File.WriteAllText(Path.Combine(Root, "bin", "kubectl"), string.Empty);
        }

        public string Root { get; }

        public void Set(string name, string? value)
        {
            _originalEnvironment.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public string WriteKubeconfig(
            string fileName,
            string currentContext,
            params (string Context, string Server)[] entries)
        {
            var path = Path.Combine(Root, "kubeconfigs", fileName);
            var clusters = string.Join(Environment.NewLine, entries.Select(entry =>
                $"  - name: {entry.Context}{Environment.NewLine}    cluster:{Environment.NewLine}      server: {entry.Server}{Environment.NewLine}      insecure-skip-tls-verify: true"));
            var contexts = string.Join(Environment.NewLine, entries.Select(entry =>
                $"  - name: {entry.Context}{Environment.NewLine}    context:{Environment.NewLine}      cluster: {entry.Context}{Environment.NewLine}      user: {entry.Context}"));
            var users = string.Join(Environment.NewLine, entries.Select(entry =>
                $"  - name: {entry.Context}{Environment.NewLine}    user:{Environment.NewLine}      token: test-only"));
            File.WriteAllText(path, $"""
                apiVersion: v1
                kind: Config
                clusters:
                {clusters}
                contexts:
                {contexts}
                current-context: {currentContext}
                users:
                {users}
                """);
            return path;
        }

        public string WriteState(string kubeconfigPath, string context)
        {
            var path = Path.Combine(Root, "state.json");
            var state = new
            {
                schemaVersion = 2,
                managedBy = "pole-kubernetes-lab",
                instanceId = new string('a', 32),
                profile = "minimal",
                phase = "ready",
                runtime = DockerRuntime(),
                overlays = Array.Empty<string>(),
                tools = new { kubectlPath = Path.Combine(Root, "bin", "kubectl") },
                registry = new
                {
                    containerId = new string('b', 64),
                    host = "127.0.0.1:5051",
                    clusterHost = "k3d-pole-lab-registry:5051"
                },
                services = new { },
                targets = new[]
                {
                    new
                    {
                        key = "default",
                        clusterName = "pole-lab-minimal",
                        kubeconfigPath,
                        context,
                        apiPort = 6551,
                        storageClassName = "local-path",
                        publicBaseDomain = "default.apps.localhost",
                        capabilities = new[] { "api", "dns", "ingress", "storage", "registry" },
                        ingress = new
                        {
                            httpBaseUri = "http://127.0.0.1:8088",
                            httpsBaseUri = "https://127.0.0.1:8448"
                        }
                    }
                }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(state));
            return path;
        }

        public string WriteFullState()
        {
            var kubectlPath = Path.Combine(Root, "bin", "kubectl");
            var buildctlPath = Path.Combine(Root, "bin", "buildctl");
            var credentialsPath = Path.Combine(Root, "generated", "harbor", "default-credentials.json");
            Directory.CreateDirectory(Path.GetDirectoryName(credentialsPath)!);
            File.WriteAllText(buildctlPath, string.Empty);
            File.WriteAllText(credentialsPath, "{\"username\":\"admin\",\"password\":\"test-only\"}");

            object Target(
                string key,
                string clusterName,
                int apiPort,
                int httpPort,
                int httpsPort,
                string[] capabilities)
            {
                var context = $"k3d-{clusterName}";
                var kubeconfig = WriteKubeconfig(
                    $"{clusterName}.yaml",
                    context,
                    (context, $"https://127.0.0.1:{apiPort}"));
                return new
                {
                    key,
                    clusterName,
                    kubeconfigPath = kubeconfig,
                    context,
                    apiPort,
                    storageClassName = "local-path",
                    publicBaseDomain = $"{key}.apps.localhost",
                    capabilities,
                    ingress = new
                    {
                        httpBaseUri = $"http://127.0.0.1:{httpPort}",
                        httpsBaseUri = $"https://127.0.0.1:{httpsPort}"
                    }
                };
            }

            var baseline = new[] { "api", "dns", "ingress", "storage", "registry", "multi-cluster" };
            var path = Path.Combine(Root, "state.json");
            var state = new
            {
                schemaVersion = 2,
                managedBy = "pole-kubernetes-lab",
                instanceId = new string('a', 32),
                profile = "full",
                phase = "ready",
                runtime = DockerRuntime(),
                overlays = new[] { "monitoring", "cert-manager", "cnpg", "harbor", "buildkit" },
                tools = new { kubectlPath, buildctlPath },
                registry = new
                {
                    containerId = new string('b', 64),
                    host = "127.0.0.1:5051",
                    clusterHost = "k3d-pole-lab-registry:5051"
                },
                services = new
                {
                    harbor = new
                    {
                        targetKey = "default",
                        @namespace = "pole-lab-harbor",
                        service = "harbor",
                        port = 80,
                        registryHost = "harbor.pole.localhost:5002",
                        credentialsPath
                    },
                    buildkit = new
                    {
                        targetKey = "default",
                        @namespace = "pole-lab-build",
                        service = "buildkit",
                        port = 1234
                    }
                },
                targets = new[]
                {
                    Target("default", "pole-lab-default", 6552, 8089, 8449,
                        [.. baseline, "topology", "monitoring", "cert-manager", "cnpg", "harbor", "buildkit"]),
                    Target("workload-a", "pole-lab-workload-a", 6553, 8090, 8450,
                        [.. baseline, "monitoring"]),
                    Target("workload-b", "pole-lab-workload-b", 6554, 8091, 8451,
                        [.. baseline, "monitoring"])
                }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(state));
            return path;
        }

        private static object DockerRuntime() => new
        {
            name = "docker",
            rootless = false,
            dockerHost = (string?)null,
            dockerSocketPath = (string?)null,
            testcontainersRyukDisabled = false
        };

        public void Dispose()
        {
            foreach (var (name, value) in _originalEnvironment)
                Environment.SetEnvironmentVariable(name, value);
            Directory.Delete(Root, recursive: true);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "kubernetes-lab-environment";
}
