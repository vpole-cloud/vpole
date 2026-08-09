using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceManifestTests
{
    [Fact]
    public void ToConfigMap_UsesEnvironmentVariables_AndManagedMetadata()
    {
        var manifest = CreateSubject();

        var configMap = manifest.ToConfigMap();

        Assert.Equal("api-gateway-config", configMap.Metadata.Name);
        Assert.Equal("pole-apps", configMap.Metadata.NamespaceProperty);
        Assert.Equal("Development", configMap.Data["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("pole-server", configMap.Metadata.Annotations["app.kubernetes.io/managed-by"]);
        Assert.Equal("web-service-1", configMap.Metadata.Annotations["pole.server/webServiceId"]);
    }

    [Fact]
    public void ToDeployment_UsesImagePortsProbesAndResolvedCompute()
    {
        var manifest = CreateSubject();

        var deployment = manifest.ToDeployment();
        var container = Assert.Single(deployment.Spec.Template.Spec.Containers);

        Assert.Equal("api-gateway", deployment.Metadata.Name);
        Assert.Equal("pole-apps", deployment.Metadata.NamespaceProperty);
        Assert.Equal(2, deployment.Spec.Replicas);
        Assert.Equal("registry.example.com/platform/api-gateway:2026.04.1", container.Image);
        Assert.Equal("regcred", Assert.Single(deployment.Spec.Template.Spec.ImagePullSecrets).Name);
        Assert.Equal(80, Assert.Single(container.Ports).ContainerPort);
        Assert.Equal("api-gateway-config", Assert.Single(container.EnvFrom).ConfigMapRef.Name);
        Assert.Equal("/health/startup", container.StartupProbe.HttpGet.Path);
        Assert.Equal("/health/live", container.LivenessProbe.HttpGet.Path);
        Assert.Equal("/health/ready", container.ReadinessProbe.HttpGet.Path);
        Assert.Equal("1", container.Resources.Requests["cpu"].ToString());
        Assert.Equal("256Mi", container.Resources.Requests["memory"].ToString());
        Assert.Equal("2", container.Resources.Limits["cpu"].ToString());
        Assert.Equal("512Mi", container.Resources.Limits["memory"].ToString());
        Assert.Equal("1", container.Resources.Requests["nvidia.com/gpu"].ToString());
        Assert.Equal(
            "linux",
            deployment.Spec.Template.Spec.NodeSelector["kubernetes.io/os"]);
        Assert.Equal(
            "workload-runtime",
            deployment.Spec.Template.Spec.RuntimeClassName);
    }

    [Fact]
    public void ToDeployment_WithDeploymentMetadata_AddsReleaseAnnotationsToPodTemplate()
    {
        var manifest = CreateSubject();
        var requestedAt = new DateTime(2026, 7, 7, 12, 30, 0, DateTimeKind.Utc);

        var deployment = manifest.ToDeployment(
            WebServiceReleaseDeploymentMetadata.Create(
                "release-7",
                7,
                requestedAt));

        var annotations = deployment.Spec.Template.Metadata.Annotations;
        Assert.Equal("web-service-1", annotations["pole.server/webServiceId"]);
        Assert.Equal("release-7", annotations["pole.server/releaseId"]);
        Assert.Equal("7", annotations["pole.server/revision"]);
        Assert.Equal("2026-07-07T12:30:00.0000000Z", annotations["pole.server/deploymentRequestedAt"]);
    }

    [Fact]
    public void ToDeployment_WithResourceDeploymentMetadata_AddsPlacementAnnotations()
    {
        var manifest = CreateSubject();
        var requestedAt = new DateTime(2026, 7, 7, 12, 30, 0, DateTimeKind.Utc);
        var metadata = WebServiceResourceDeploymentMetadata.Create(
            "release-4",
            "deployment-7",
            "runtime-cell-7",
            "target-7",
            desiredGeneration: 9,
            logicalRevision: 4,
            requestedAt);

        var deployment = manifest.ToDeployment(metadata);

        var annotations = deployment.Spec.Template.Metadata.Annotations;
        Assert.Equal("deployment-7", annotations["pole.server/resourceDeploymentId"]);
        Assert.Equal("release-4", annotations["pole.server/releaseId"]);
        Assert.Equal("runtime-cell-7", annotations["pole.server/runtimeCellId"]);
        Assert.Equal("target-7", annotations["pole.server/deploymentTargetId"]);
        Assert.Equal("9", annotations["pole.server/deploymentGeneration"]);
        Assert.Equal("4", annotations["pole.server/revision"]);
    }

    [Fact]
    public void ToService_UsesConfiguredPorts_AndLabels()
    {
        var manifest = CreateSubject();

        var service = manifest.ToService();

        Assert.Equal("api-gateway-svc", service.Metadata.Name);
        Assert.Equal("pole-apps", service.Metadata.NamespaceProperty);
        Assert.Equal("ClusterIP", service.Spec.Type);

        var ports = service.Spec.Ports;
        Assert.Equal(2, ports.Count);
        Assert.Equal("p0-80", ports[0].Name);
        Assert.Equal(80, ports[0].Port);
        Assert.Equal("8080", ports[0].TargetPort.Value);
        Assert.Equal("TCP", ports[0].Protocol);
        Assert.Equal("p1-9090", ports[1].Name);
        Assert.Equal(9090, ports[1].Port);
        Assert.Equal("metrics", ports[1].TargetPort.Value);
        Assert.Equal("TCP", ports[1].Protocol);
        Assert.Equal("api-gateway", service.Spec.Selector["app"]);
    }

    [Fact]
    public void ToIngress_UsesResolvedTargetConfigurationAndFirstServicePort()
    {
        var manifest = CreateSubject();

        var ingress = manifest.ToIngress();
        var rule = Assert.Single(ingress.Spec.Rules);
        var path = Assert.Single(rule.Http.Paths);
        var tls = Assert.Single(ingress.Spec.Tls);

        Assert.Equal("api-gateway-ing", ingress.Metadata.Name);
        Assert.Equal("pole-apps", ingress.Metadata.NamespaceProperty);
        Assert.Equal("nginx-private", ingress.Spec.IngressClassName);
        Assert.Equal("api.example.com", rule.Host);
        Assert.Equal("api-gateway-svc", path.Backend.Service.Name);
        Assert.Equal(80, path.Backend.Service.Port.Number);
        Assert.Equal("pole-private-issuer", ingress.Metadata.Annotations["cert-manager.io/cluster-issuer"]);
        Assert.Equal("api.example.com", Assert.Single(tls.Hosts));
        Assert.Equal("api-gateway-tls", tls.SecretName);
    }

    [Fact]
    public void ToServiceMonitor_TargetsTheGeneratedServicePort()
    {
        var manifest = CreateSubject();

        var serviceMonitor = JsonSerializer.SerializeToNode(manifest.ToServiceMonitor())!.AsObject();
        var metadata = serviceMonitor["metadata"]!.AsObject();
        var spec = serviceMonitor["spec"]!.AsObject();
        var selector = spec["selector"]!.AsObject()["matchLabels"]!.AsObject();
        var namespaceSelector = spec["namespaceSelector"]!.AsObject()["matchNames"]!.AsArray();
        var endpoint = spec["endpoints"]!.AsArray()[0]!.AsObject();

        Assert.Equal("api-gateway-sm", metadata["name"]!.GetValue<string>());
        Assert.Equal("pole-apps", metadata["namespace"]!.GetValue<string>());
        Assert.Equal("api-gateway", selector["app"]!.GetValue<string>());
        Assert.Equal("pole-apps", namespaceSelector[0]!.GetValue<string>());
        Assert.Equal("p0-80", endpoint["port"]!.GetValue<string>());
        Assert.Equal("/metrics", endpoint["path"]!.GetValue<string>());
    }

    [Fact]
    public void ToImagePullSecret_ReturnsDockerConfigJson_WhenSecretIsConfigured()
    {
        var manifest = CreateSubject();

        var secret = manifest.ToImagePullSecret("pole-user", "pole-pass");

        Assert.NotNull(secret);
        Assert.Equal("regcred", secret.Metadata.Name);
        Assert.Equal("pole-apps", secret.Metadata.NamespaceProperty);
        Assert.Equal("kubernetes.io/dockerconfigjson", secret.Type);

        var dockerConfig = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(secret.Data[".dockerconfigjson"]));
        var auths = dockerConfig.GetProperty("auths");
        var registry = auths.GetProperty("registry.example.com");

        Assert.Equal("pole-user", registry.GetProperty("username").GetString());
        Assert.Equal("pole-pass", registry.GetProperty("password").GetString());
    }

    [Fact]
    public void ToImagePullSecret_ReturnsNull_WhenSecretIsNotConfigured()
    {
        var manifest = CreateSubject(imageSecretName: null);

        Assert.Null(manifest.ToImagePullSecret());
    }

    [Fact]
    public void ToHorizontalPodAutoscaler_ReturnsNull_WhenAutoscalingIsDisabled()
    {
        var manifest = CreateSubject();

        Assert.Null(manifest.ToHorizontalPodAutoscaler());
    }

    [Fact]
    public void ToHorizontalPodAutoscaler_UsesAutoscalingValues_WhenConfigured()
    {
        var manifest = CreateSubject(autoscaling: new AutoscalingSpec(
            MinReplicas: 2,
            MaxReplicas: 5,
            Metric: new AutoscalingMetric("cpu", 80)));

        var hpa = manifest.ToHorizontalPodAutoscaler();

        Assert.NotNull(hpa);
        Assert.Equal("api-gateway-hpa", hpa.Metadata.Name);
        Assert.Equal("pole-apps", hpa.Metadata.NamespaceProperty);
        Assert.Equal("api-gateway", hpa.Spec.ScaleTargetRef.Name);
        Assert.Equal(2, hpa.Spec.MinReplicas);
        Assert.Equal(5, hpa.Spec.MaxReplicas);
    }

    private static WebServiceInKubernetes CreateSubject(
        string? imageSecretName = "regcred",
        AutoscalingSpec? autoscaling = null)
    {
        var spec = TestData.ValidWebServiceSpec().ToDomain() with
        {
            Image = new ImageInfo("registry.example.com", "platform/api-gateway", "2026.04.1", imageSecretName),
            Config = new WebServiceConfig(
                EnvironmentVariables: new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["LOG_LEVEL"] = "Debug"
                }.ToImmutableDictionary(),
                PortMappings:
                [
                    new PortMapping(80, "8080", "TCP", "http"),
                    new PortMapping(9090, "metrics", "TCP", "metrics")
                ]),
            Probes = new ProbeSpec(
                Startup: new ProbeConfig("/health/startup", "8080"),
                Liveness: new ProbeConfig("/health/live", "8080"),
                Readiness: new ProbeConfig("/health/ready", "8080")),
            Autoscaling = autoscaling
        };

        var options = WebServiceFactory.DefaultOptions with
        {
            Id = "web-service-1"
        };

        var webService = WebServiceFactory.Create(
            name: "api-gateway",
            tenantId: TestData.TenantId,
            resourceGroupId: "rg-1",
            spec: spec,
            dnsRecord: new DnsRecordShortInfo("dns-1", "api.example.com"),
            options: options);
        return webService.ToKubernetes(
            "pole-apps",
            new ResourceDeploymentIngressConfiguration(
                "nginx-private",
                "pole-private-issuer"),
            new ResourceDeploymentComputeConfiguration(
                "507f1f77bcf86cd799439035",
                ComputeProfileRevision: 3,
                DeploymentTargetId: "507f1f77bcf86cd799439036",
                DisplayName: "Web workloads",
                new ResourceDeploymentComputeResourceValues(1000, 256),
                new ResourceDeploymentComputeResourceValues(2000, 512),
                ResourceDeploymentComputeArchitecture.Any,
                ExtendedResources:
                [
                    new ResourceDeploymentExtendedResource("nvidia.com/gpu", 1)
                ],
                NodeSelector:
                [
                    new ResourceDeploymentNodeSelector("kubernetes.io/os", "linux")
                ],
                RequiredNodeAffinity: [],
                PreferredNodeAffinity: [],
                Tolerations: [],
                RuntimeClassName: "workload-runtime"));
    }
}
