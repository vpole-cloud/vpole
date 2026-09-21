using System.Collections.Immutable;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Security;
using Pole.Functions.Protocol;
using Xunit;

namespace Pole.Features.Functions.Tests;

[Trait("Layer", "Manifest")]
[Trait("Concern", "Rendering")]
[Trait("Scope", "ManifestOnly")]
[Trait("Feature", "Functions")]
public sealed class FunctionRevisionManifestTests
{
    [Fact]
    public void Deployment_is_revision_specific_zero_scaled_and_hardened()
    {
        var subject = CreateSubject();

        var deployment = subject.ToDeployment();
        var pod = deployment.Spec.Template.Spec;
        var container = Assert.Single(pod.Containers);

        Assert.Equal("fn-507f1f77bcf86cd799439011-r3", deployment.Metadata.Name);
        Assert.Equal("pole-tenant-rg", deployment.Metadata.NamespaceProperty);
        Assert.Equal(0, deployment.Spec.Replicas);
        Assert.Equal(
            "0",
            deployment.Metadata.Annotations[FunctionRevisionInKubernetes.ScaleGenerationAnnotation]);
        Assert.Equal(1, deployment.Spec.RevisionHistoryLimit);
        Assert.Equal("Recreate", deployment.Spec.Strategy.Type);
        Assert.Equal(subject.App.Id, deployment.Metadata.Labels["pole.io/function-app-id"]);
        Assert.Equal(subject.Revision.Id, deployment.Metadata.Labels["pole.io/function-revision-id"]);
        Assert.Equal("function-app", deployment.Metadata.Labels["pole.io/resource-type"]);
        Assert.Equal(
            subject.Placement.IsolationScopeId,
            deployment.Spec.Template.Metadata.Labels["pole.io/isolation-scope-id"]);
        Assert.Equal(
            subject.Placement.ResourceDeploymentId,
            deployment.Spec.Template.Metadata.Labels["pole.io/resource-deployment-id"]);
        Assert.Equal(deployment.Spec.Template.Metadata.Labels, subject.ToService().Metadata.Labels);
        Assert.Equal(deployment.Spec.Selector.MatchLabels, deployment.Spec.Template.Metadata.Labels
            .Where(pair => deployment.Spec.Selector.MatchLabels.ContainsKey(pair.Key))
            .ToDictionary());

        Assert.Equal("registry.example/team/functions@sha256:abcdef", container.Image);
        Assert.Equal("Always", container.ImagePullPolicy);
        Assert.Equal("function-pull-secret", Assert.Single(pod.ImagePullSecrets).Name);
        Assert.Equal(FunctionRevisionInKubernetes.AgentPort, Assert.Single(container.Ports).ContainerPort);
        Assert.Equal(PoleFunctionProtocol.LivenessPath, container.StartupProbe.HttpGet.Path);
        Assert.Equal(PoleFunctionProtocol.LivenessPath, container.LivenessProbe.HttpGet.Path);
        Assert.Equal(PoleFunctionProtocol.ReadinessPath, container.ReadinessProbe.HttpGet.Path);
        Assert.Equal("250m", container.Resources.Requests["cpu"].ToString());
        Assert.Equal("256Mi", container.Resources.Requests["memory"].ToString());
        Assert.Equal("500m", container.Resources.Limits["cpu"].ToString());
        Assert.Equal("512Mi", container.Resources.Limits["memory"].ToString());

        Assert.False(pod.AutomountServiceAccountToken);
        Assert.False(pod.EnableServiceLinks);
        Assert.True(pod.SecurityContext.RunAsNonRoot);
        Assert.Equal("RuntimeDefault", pod.SecurityContext.SeccompProfile.Type);
        Assert.True(container.SecurityContext.RunAsNonRoot);
        Assert.False(container.SecurityContext.AllowPrivilegeEscalation);
        Assert.True(container.SecurityContext.ReadOnlyRootFilesystem);
        Assert.Contains("ALL", container.SecurityContext.Capabilities.Drop);
        Assert.Equal("/tmp", Assert.Single(container.VolumeMounts).MountPath);
        Assert.NotNull(Assert.Single(pod.Volumes).EmptyDir);
    }

    [Fact]
    public void Deployment_maps_runtime_identity_concurrency_config_and_secret_references()
    {
        var subject = CreateSubject();
        var environment = Assert.Single(subject.ToDeployment().Spec.Template.Spec.Containers)
            .Env.ToDictionary(item => item.Name);

        Assert.Equal(subject.App.Id, environment["PoleFunctions__FunctionAppId"].Value);
        Assert.Equal(subject.Revision.Id, environment["PoleFunctions__RevisionId"].Value);
        Assert.Equal(subject.App.TenantId, environment["PoleFunctions__TenantId"].Value);
        Assert.Equal("8", environment["PoleFunctions__MaxConcurrencyPerInstance"].Value);
        Assert.Equal("4", environment["PoleFunctions__HandlerConcurrencyPerInstance__webhook"].Value);
        Assert.Equal("Debug", environment["LOG_LEVEL"].Value);
        Assert.Equal("function-secrets", environment["API_KEY"].ValueFrom.SecretKeyRef.Name);
        Assert.Equal("API_KEY", environment["API_KEY"].ValueFrom.SecretKeyRef.Key);
    }

    [Fact]
    public void Deployment_injects_only_public_runtime_trust_and_pod_identity()
    {
        var subject = CreateSubject();
        var trust = new FunctionAgentTrustBundle(
            "pole-runtime",
            "pole-agent",
            [new FunctionAgentTrustedKey("key-1", "public-spki")]);

        var environment = Assert.Single(subject.ToDeployment(trust).Spec.Template.Spec.Containers)
            .Env.ToDictionary(item => item.Name);

        Assert.Equal("pole-runtime", environment["PoleFunctions__AgentAuthentication__Issuer"].Value);
        Assert.Equal("pole-agent", environment["PoleFunctions__AgentAuthentication__Audience"].Value);
        Assert.Equal(
            "metadata.uid",
            environment["PoleFunctions__AgentAuthentication__TargetUid"].ValueFrom.FieldRef.FieldPath);
        Assert.Equal(
            "key-1",
            environment["PoleFunctions__AgentAuthentication__TrustedKeys__0__KeyId"].Value);
        Assert.Equal(
            "public-spki",
            environment["PoleFunctions__AgentAuthentication__TrustedKeys__0__SubjectPublicKeyInfoBase64"].Value);
        Assert.DoesNotContain(environment.Keys, key => key.Contains("Private", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Service_is_cluster_internal_and_selects_only_the_revision()
    {
        var subject = CreateSubject();

        var service = subject.ToService();

        Assert.Equal("fn-507f1f77bcf86cd799439011-r3-agent", service.Metadata.Name);
        Assert.Equal("ClusterIP", service.Spec.Type);
        Assert.False(service.Spec.PublishNotReadyAddresses);
        Assert.Equal(subject.App.Id, service.Spec.Selector["pole.io/function-app-id"]);
        Assert.Equal(subject.Revision.Id, service.Spec.Selector["pole.io/function-revision-id"]);
        var port = Assert.Single(service.Spec.Ports);
        Assert.Equal(FunctionRevisionInKubernetes.AgentPort, port.Port);
        Assert.Equal(FunctionRevisionInKubernetes.AgentPort.ToString(), port.TargetPort.Value);
    }

    [Fact]
    public void Resource_name_is_dns_safe_and_bounded()
    {
        var name = FunctionRevisionInKubernetes.BuildResourceName(
            "507F1F77BCF86CD799439011-extra-data-that-must-not-leak-into-the-name",
            12345);

        Assert.Equal("fn-507f1f77bcf86cd799439011-r12345", name);
        Assert.True(name.Length <= 63);
    }

    private static FunctionRevisionInKubernetes CreateSubject()
    {
        var now = DateTimeOffset.Parse("2026-07-17T10:00:00Z");
        var spec = new FunctionRevisionSpecInput(
            new FunctionImage(
                "registry.example",
                "team/functions",
                "v3",
                "sha256:abcdef",
                "function-pull-secret"),
            PoleFunctionProtocol.Version,
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty.Add("LOG_LEVEL", "Debug"),
                ImmutableDictionary<string, string>.Empty.Add("API_KEY", "function-secrets")),
            new FunctionResources(
                new FunctionResourceValues(250, 256),
                new FunctionResourceValues(500, 512)),
            new FunctionScalingPolicy(0, 5, 4, 8, 60, 30),
            [
                new FunctionHandlerSpec(
                    "webhook",
                    4,
                    new HttpFunctionTriggerSpec(
                        string.Empty,
                        "orders/{id}",
                        ["POST"],
                        FunctionExposure.Public))
            ]);
        var created = FunctionAppFactory.Create(
            "507f1f77bcf86cd799439011",
            "resource-group-1",
            new FunctionPlacementIdentity(
                "507f1f77bcf86cd799439091",
                "507f1f77bcf86cd799439092",
                "507f1f77bcf86cd799439093"),
            "order-functions",
            "orders",
            spec,
            [],
            now);
        var app = created.FunctionApp with
        {
            Id = "507f1f77bcf86cd799439011"
        };
        var revision = created.Revision with
        {
            Id = "507f1f77bcf86cd799439012",
            FunctionAppId = app.Id,
            RevisionNumber = 3
        };
        return new FunctionRevisionInKubernetes(
            app,
            revision,
            new FunctionResourcePlacement(
                app.Placement.ResourceDeploymentId,
                app.Placement.RuntimeCellId,
                app.Placement.DeploymentTargetId,
                "pole-tenant-rg",
                "507f1f77bcf86cd799439044"));
    }
}
