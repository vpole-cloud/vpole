using System.Collections.ObjectModel;
using System.Text.Json;
using k8s;

namespace Pole.Testing;

public sealed record KubernetesLabRegistry(
    string ContainerId,
    string Host,
    string ClusterHost
);

public sealed record KubernetesLabRuntime(
    string Name,
    bool Rootless,
    string? DockerHost,
    string? DockerSocketPath,
    bool TestcontainersRyukDisabled
);

public sealed record KubernetesLabIngress(
    Uri HttpBaseUri,
    Uri HttpsBaseUri
);

public sealed record KubernetesLabHarbor(
    string TargetKey,
    string Namespace,
    string Service,
    int Port,
    string RegistryHost,
    string CredentialsPath
);

public sealed record KubernetesLabBuildKit(
    string TargetKey,
    string Namespace,
    string Service,
    int Port
);

public sealed record KubernetesLabServices(
    KubernetesLabHarbor Harbor,
    KubernetesLabBuildKit BuildKit
);

public sealed record KubernetesLabTarget(
    string Key,
    string ClusterName,
    string KubeconfigPath,
    string Context,
    string KubectlPath,
    string StorageClassName,
    string PublicBaseDomain,
    IReadOnlySet<string> Capabilities,
    KubernetesLabIngress Ingress
)
{
    public IReadOnlyList<string> KubectlSelectionArguments => ["--kubeconfig", KubeconfigPath, "--context", Context];

    public KubernetesClientConfiguration CreateConfiguration() =>
        KubernetesClientConfiguration.BuildConfigFromConfigFile(
            new FileInfo(KubeconfigPath),
            currentContext: Context,
            masterUrl: null,
            useRelativePaths: true);

    public bool HasCapability(string capability) => Capabilities.Contains(capability.Trim());
}

public sealed class KubernetesLabState
{
    public const int CurrentSchemaVersion = 2;
    public const string ManagedByValue = "pole-kubernetes-lab";

    private readonly IReadOnlyDictionary<string, KubernetesLabTarget> _targetsByKey;

    private KubernetesLabState(
        string statePath,
        string instanceId,
        string profile,
        KubernetesLabRuntime runtime,
        KubernetesLabRegistry registry,
        KubernetesLabServices? services,
        string? buildctlPath,
        IReadOnlyList<KubernetesLabTarget> targets)
    {
        StatePath = statePath;
        InstanceId = instanceId;
        Profile = profile;
        Runtime = runtime;
        Registry = registry;
        Services = services;
        BuildctlPath = buildctlPath;
        Targets = targets;
        _targetsByKey = new ReadOnlyDictionary<string, KubernetesLabTarget>(
            targets.ToDictionary(target => target.Key, StringComparer.Ordinal));
    }

    public string StatePath { get; }
    public string InstanceId { get; }
    public string Profile { get; }
    public KubernetesLabRuntime Runtime { get; }
    public KubernetesLabRegistry Registry { get; }
    public KubernetesLabServices? Services { get; }
    public string? BuildctlPath { get; }
    public IReadOnlyList<KubernetesLabTarget> Targets { get; }

    public KubernetesLabTarget GetRequiredTarget(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _targetsByKey.TryGetValue(key.Trim(), out var target)
            ? target
            : throw new InvalidOperationException(
                $"Kubernetes lab target '{key.Trim()}' does not exist in profile '{Profile}'.");
    }

    public static KubernetesLabState Load(string statePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        if (!Path.IsPathFullyQualified(statePath))
            throw new InvalidOperationException("POLE_K8S_LAB_STATE must be an absolute path.");

        var fullStatePath = Path.GetFullPath(statePath);
        var stateFile = new FileInfo(fullStatePath);
        if (!stateFile.Exists)
            throw new FileNotFoundException("The configured Kubernetes lab state file does not exist.", fullStatePath);
        if (stateFile.LinkTarget is not null)
            throw new InvalidOperationException("The Kubernetes lab state file must not be a symbolic link.");

        using var stream = stateFile.OpenRead();
        using var document = JsonDocument.Parse(stream);
        var root = RequireObject(document.RootElement, "state");
        var schemaVersion = RequireInt32(root, "schemaVersion");
        if (schemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Kubernetes lab state schema {schemaVersion} is not supported; expected {CurrentSchemaVersion}.");
        if (!string.Equals(RequireString(root, "managedBy"), ManagedByValue, StringComparison.Ordinal))
            throw new InvalidOperationException("The Kubernetes lab state ownership marker is invalid.");
        var instanceId = RequireHexIdentifier(root, "instanceId", 32);
        if (!string.Equals(RequireString(root, "phase"), "ready", StringComparison.Ordinal))
            throw new InvalidOperationException("The Kubernetes lab state is not ready.");

        var profile = RequireString(root, "profile");
        var stateRoot = stateFile.DirectoryName ??
            throw new InvalidOperationException("The Kubernetes lab state has no parent directory.");
        var expectedTargets = ValidateProfileContract(root, stateRoot, profile);
        var runtime = ParseRuntime(root);
        var tools = RequireObject(RequireProperty(root, "tools"), "tools");
        var kubectlPath = RequireOwnedAbsoluteFile(stateRoot, RequireString(tools, "kubectlPath"), "kubectl");
        var buildctlPath = tools.TryGetProperty("buildctlPath", out var configuredBuildctl)
            ?
            RequireOwnedAbsoluteFile(stateRoot, RequireString(tools, "buildctlPath"), "buildctl")
            : profile == "full"
                ? throw new InvalidOperationException(
                    "The full Kubernetes lab state is missing the pinned buildctl path.")
                : null;
        var registryElement = RequireObject(RequireProperty(root, "registry"), "registry");
        var registry = new KubernetesLabRegistry(
            RequireHexIdentifier(registryElement, "containerId", 64),
            RequireString(registryElement, "host"),
            RequireString(registryElement, "clusterHost"));
        var services = ParseServices(root, stateRoot, profile);

        var targetElements = RequireProperty(root, "targets");
        if (targetElements.ValueKind != JsonValueKind.Array || targetElements.GetArrayLength() == 0)
            throw new InvalidOperationException("The Kubernetes lab state must contain at least one target.");

        var targets = targetElements.EnumerateArray()
            .Select((target, index) => ParseTarget(
                RequireObject(target, "target"),
                stateRoot,
                kubectlPath,
                expectedTargets[index].ApiPort))
            .ToArray();
        if (targets.Select(target => target.Key).Distinct(StringComparer.Ordinal).Count() != targets.Length)
            throw new InvalidOperationException("The Kubernetes lab state contains duplicate target keys.");
        if (targets.Select(target => target.ClusterName).Distinct(StringComparer.Ordinal).Count() != targets.Length)
            throw new InvalidOperationException("The Kubernetes lab state contains duplicate cluster names.");

        return new KubernetesLabState(
            fullStatePath,
            instanceId,
            profile,
            runtime,
            registry,
            services,
            buildctlPath,
            targets);
    }

    private static KubernetesLabRuntime ParseRuntime(JsonElement root)
    {
        var element = RequireObject(RequireProperty(root, "runtime"), "runtime");
        var name = RequireString(element, "name");
        var rootless = RequireBoolean(element, "rootless");
        var dockerHost = ReadOptionalString(element, "dockerHost");
        var dockerSocketPath = ReadOptionalString(element, "dockerSocketPath");
        var ryukDisabled = RequireBoolean(element, "testcontainersRyukDisabled");

        if (name is not ("docker" or "podman"))
            throw new InvalidOperationException($"Kubernetes lab runtime '{name}' is not supported.");
        if (dockerHost is not null && !Uri.TryCreate(dockerHost, UriKind.Absolute, out _))
            throw new InvalidOperationException("Kubernetes lab runtime Docker host must be an absolute URI.");
        if (dockerSocketPath is not null && !Path.IsPathFullyQualified(dockerSocketPath))
            throw new InvalidOperationException("Kubernetes lab runtime Docker socket path must be absolute.");
        if (name == "podman" && (dockerHost is null || !dockerHost.StartsWith("unix://", StringComparison.Ordinal) ||
                dockerSocketPath is null || dockerHost != $"unix://{dockerSocketPath}" || !ryukDisabled))
            throw new InvalidOperationException(
                "Kubernetes lab Podman runtime must expose its Unix Docker API socket and disable Testcontainers Ryuk.");
        if (name == "docker" && ryukDisabled)
            throw new InvalidOperationException(
                "Kubernetes lab Docker runtime must retain Testcontainers Ryuk cleanup.");

        return new KubernetesLabRuntime(name, rootless, dockerHost, dockerSocketPath, ryukDisabled);
    }

    private static KubernetesLabServices? ParseServices(
        JsonElement root,
        string stateRoot,
        string profile)
    {
        if (!root.TryGetProperty("services", out var servicesElement))
            return profile == "full"
                ? throw new InvalidOperationException("The full Kubernetes lab state is missing shared services.")
                : null;

        var services = RequireObject(servicesElement, "services");
        if (profile != "full" && !services.EnumerateObject().Any())
            return null;

        var harbor = RequireObject(RequireProperty(services, "harbor"), "Harbor service");
        var buildKit = RequireObject(RequireProperty(services, "buildkit"), "BuildKit service");
        var parsed = new KubernetesLabServices(
            new KubernetesLabHarbor(
                RequireString(harbor, "targetKey"),
                RequireString(harbor, "namespace"),
                RequireString(harbor, "service"),
                RequireInt32(harbor, "port"),
                RequireString(harbor, "registryHost"),
                RequireOwnedAbsoluteFile(stateRoot, RequireString(harbor, "credentialsPath"), "Harbor credentials")),
            new KubernetesLabBuildKit(
                RequireString(buildKit, "targetKey"),
                RequireString(buildKit, "namespace"),
                RequireString(buildKit, "service"),
                RequireInt32(buildKit, "port")));

        if (parsed.Harbor.TargetKey != "default" || parsed.Harbor.Namespace != "pole-lab-harbor" ||
            parsed.Harbor.Service != "harbor" || parsed.Harbor.Port != 80 ||
            parsed.Harbor.RegistryHost != "harbor.pole.localhost:5002" || parsed.BuildKit.TargetKey != "default" ||
            parsed.BuildKit.Namespace != "pole-lab-build" || parsed.BuildKit.Service != "buildkit" ||
            parsed.BuildKit.Port != 1234)
            throw new InvalidOperationException("Kubernetes lab shared services do not match the checked-in contract.");

        return parsed;
    }

    private static KubernetesLabTarget ParseTarget(
        JsonElement target,
        string stateRoot,
        string kubectlPath,
        int expectedApiPort)
    {
        var key = RequireString(target, "key");
        var clusterName = RequireString(target, "clusterName");
        var kubeconfigPath = RequireOwnedAbsoluteFile(
            stateRoot,
            RequireString(target, "kubeconfigPath"),
            $"target '{key}' kubeconfig");
        var context = RequireString(target, "context");
        if (!string.Equals(context, $"k3d-{clusterName}", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Kubernetes lab target '{key}' has an unexpected context for cluster '{clusterName}'.");

        ValidateKubeconfigContext(kubeconfigPath, context, key);
        var configuration = KubernetesClientConfiguration.BuildConfigFromConfigFile(
            new FileInfo(kubeconfigPath),
            currentContext: context,
            masterUrl: null,
            useRelativePaths: true);
        if (!Uri.TryCreate(configuration.Host, UriKind.Absolute, out var server) || !server.IsLoopback ||
            server.Port != expectedApiPort)
            throw new InvalidOperationException(
                $"Kubernetes lab target '{key}' kubeconfig server does not match its checked-in loopback API endpoint.");
        var capabilitiesElement = RequireProperty(target, "capabilities");
        if (capabilitiesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Kubernetes lab target '{key}' capabilities must be an array.");
        var capabilities = capabilitiesElement.EnumerateArray()
            .Select(capability => capability.ValueKind == JsonValueKind.String ? capability.GetString() : null)
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Select(capability => capability!)
            .ToHashSet(StringComparer.Ordinal);
        var ingress = RequireObject(RequireProperty(target, "ingress"), $"target '{key}' ingress");

        return new KubernetesLabTarget(
            key,
            clusterName,
            kubeconfigPath,
            context,
            kubectlPath,
            RequireString(target, "storageClassName"),
            RequireString(target, "publicBaseDomain"),
            capabilities,
            new KubernetesLabIngress(
                RequireAbsoluteUri(ingress, "httpBaseUri"),
                RequireAbsoluteUri(ingress, "httpsBaseUri")));
    }

    private static void ValidateKubeconfigContext(
        string kubeconfigPath,
        string context,
        string key)
    {
        using var stream = File.OpenRead(kubeconfigPath);
        var kubeconfig = KubernetesClientConfiguration.LoadKubeConfig(stream);
        if (kubeconfig.Contexts is null ||
            !kubeconfig.Contexts.Any(item => string.Equals(item.Name, context, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"Kubernetes lab target '{key}' context does not exist in its standalone kubeconfig.");
    }

    private static IReadOnlyList<ExpectedTarget> ValidateProfileContract(
        JsonElement root,
        string stateRoot,
        string profile)
    {
        var expected = profile switch
        {
            "minimal" => new[]
            {
                new ExpectedTarget("default", "pole-lab-minimal", 6551, 8088, 8448),
            },
            "multi" => new[]
            {
                new ExpectedTarget("default", "pole-lab-default", 6552, 8089, 8449),
                new ExpectedTarget("workload-a", "pole-lab-workload-a", 6553, 8090, 8450),
                new ExpectedTarget("workload-b", "pole-lab-workload-b", 6554, 8091, 8451),
            },
            "full" => new[]
            {
                new ExpectedTarget("default", "pole-lab-default", 6552, 8089, 8449),
                new ExpectedTarget("workload-a", "pole-lab-workload-a", 6553, 8090, 8450),
                new ExpectedTarget("workload-b", "pole-lab-workload-b", 6554, 8091, 8451),
            },
            "topology" => new[]
            {
                new ExpectedTarget("default", "pole-lab-topology", 6555, 8092, 8452),
            },
            _ => throw new InvalidOperationException($"Unknown Kubernetes lab profile '{profile}'."),
        };

        var overlaysElement = RequireProperty(root, "overlays");
        if (overlaysElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Kubernetes lab state 'overlays' must be an array.");
        var overlays = overlaysElement.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .ToArray();
        var allowedOverlays = new HashSet<string>(
            ["monitoring", "cert-manager", "cnpg", "harbor", "buildkit"],
            StringComparer.Ordinal);
        if (overlays.Any(item => string.IsNullOrWhiteSpace(item) || !allowedOverlays.Contains(item!)) ||
            overlays.Distinct(StringComparer.Ordinal).Count() != overlays.Length)
            throw new InvalidOperationException("Kubernetes lab state contains invalid or duplicate overlays.");
        if (profile != "full" && overlays.Any(item => item is "harbor" or "buildkit"))
            throw new InvalidOperationException(
                "Focused Kubernetes lab state cannot claim the full profile's Harbor or BuildKit overlays.");
        if (profile == "full" &&
            !overlays.Select(item => item!).ToHashSet(StringComparer.Ordinal).SetEquals(allowedOverlays))
            throw new InvalidOperationException(
                "The full Kubernetes lab state must contain the exact checked-in overlay contract.");

        var registry = RequireObject(RequireProperty(root, "registry"), "registry");
        if (RequireString(registry, "host") != "127.0.0.1:5051" ||
            RequireString(registry, "clusterHost") != "k3d-pole-lab-registry:5051")
            throw new InvalidOperationException(
                "Kubernetes lab registry endpoints do not match the checked-in contract.");

        var targets = RequireProperty(root, "targets");
        if (targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() != expected.Length)
            throw new InvalidOperationException($"Kubernetes lab profile '{profile}' has an unexpected target count.");

        for (var index = 0; index < expected.Length; index++)
        {
            var actual = RequireObject(targets[index], $"target {index}");
            var contract = expected[index];
            var expectedKubeconfig = Path.Combine(stateRoot, "kubeconfigs", $"{contract.ClusterName}.yaml");
            var ingress = RequireObject(RequireProperty(actual, "ingress"), $"target '{contract.Key}' ingress");
            var expectedCapabilities = new HashSet<string>(
                ["api", "dns", "ingress", "storage", "registry"],
                StringComparer.Ordinal);
            if (profile is "multi" or "full")
                expectedCapabilities.Add("multi-cluster");
            if (profile == "topology" || (profile == "full" && contract.Key == "default"))
                expectedCapabilities.Add("topology");
            if (profile == "full" && contract.Key != "default")
                expectedCapabilities.Add("monitoring");
            else
                foreach (var overlay in overlays)
                    expectedCapabilities.Add(overlay!);
            var actualCapabilities = ReadStringSet(actual, "capabilities");

            if (RequireString(actual, "key") != contract.Key ||
                RequireString(actual, "clusterName") != contract.ClusterName ||
                RequireString(actual, "context") != $"k3d-{contract.ClusterName}" ||
                Path.GetFullPath(RequireString(actual, "kubeconfigPath")) != Path.GetFullPath(expectedKubeconfig) ||
                RequireInt32(actual, "apiPort") != contract.ApiPort ||
                RequireString(actual, "storageClassName") != "local-path" ||
                RequireString(actual, "publicBaseDomain") != $"{contract.Key}.apps.localhost" ||
                RequireString(ingress, "httpBaseUri") != $"http://127.0.0.1:{contract.HttpPort}" ||
                RequireString(ingress, "httpsBaseUri") != $"https://127.0.0.1:{contract.HttpsPort}" ||
                !actualCapabilities.SetEquals(expectedCapabilities))
                throw new InvalidOperationException(
                    $"Kubernetes lab target '{contract.Key}' does not match the checked-in profile contract.");
        }

        return expected;
    }

    private static HashSet<string> ReadStringSet(JsonElement parent, string name)
    {
        var element = RequireProperty(parent, name);
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Kubernetes lab state '{name}' must be an array.");
        var values = element.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .ToArray();
        if (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidOperationException($"Kubernetes lab state '{name}' contains invalid or duplicate values.");
        return values.Select(value => value!).ToHashSet(StringComparer.Ordinal);
    }

    private static string RequireOwnedAbsoluteFile(
        string stateRoot,
        string path,
        string description)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"The Kubernetes lab {description} path must be absolute.");
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(stateRoot, fullPath);
        if (Path.IsPathFullyQualified(relative) || string.Equals(relative, "..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The Kubernetes lab {description} path must remain beneath the state directory.");

        var file = new FileInfo(fullPath);
        if (!file.Exists)
            throw new FileNotFoundException($"The Kubernetes lab {description} file does not exist.", fullPath);
        if (file.LinkTarget is not null)
            throw new InvalidOperationException($"The Kubernetes lab {description} file must not be a symbolic link.");
        return fullPath;
    }

    private static Uri RequireAbsoluteUri(JsonElement parent, string name)
    {
        var value = RequireString(parent, name);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException($"Kubernetes lab '{name}' must be an absolute URI.");
    }

    private static JsonElement RequireProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var property)
            ? property
            : throw new InvalidOperationException($"Kubernetes lab state is missing '{name}'.");

    private static JsonElement RequireObject(JsonElement element, string description) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw new InvalidOperationException($"Kubernetes lab {description} must be an object.");

    private static string RequireString(JsonElement parent, string name)
    {
        var property = RequireProperty(parent, name);
        var value = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Kubernetes lab state '{name}' must be a non-empty string.");
    }

    private static string RequireHexIdentifier(
        JsonElement parent,
        string name,
        int length)
    {
        var value = RequireString(parent, name);
        return value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new InvalidOperationException(
                $"Kubernetes lab state '{name}' must be exactly {length} lowercase hexadecimal characters.");
    }

    private static int RequireInt32(JsonElement parent, string name)
    {
        var property = RequireProperty(parent, name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : throw new InvalidOperationException($"Kubernetes lab state '{name}' must be an integer.");
    }

    private static bool RequireBoolean(JsonElement parent, string name)
    {
        var property = RequireProperty(parent, name);
        return property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : throw new InvalidOperationException($"Kubernetes lab state '{name}' must be boolean.");
    }

    private static string? ReadOptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        var value = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Kubernetes lab state '{name}' must be null or a non-empty string.");
    }

    private sealed record ExpectedTarget(
        string Key,
        string ClusterName,
        int ApiPort,
        int HttpPort,
        int HttpsPort
    );
}
