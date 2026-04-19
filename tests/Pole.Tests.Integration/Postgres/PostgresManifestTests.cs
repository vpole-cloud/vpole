using System.Text.Json;
using Pole.Domain.Models.FSharp;
using Pole.Features.Postgres.Kubernetes;
using Xunit;

namespace Pole.Tests.Integration.Postgres;

public sealed class PostgresManifestTests
{
    [Fact]
    public void BuildCluster_UsesNamespaceSpecAndSecretReference()
    {
        var builder = CreateSubject();
        var clusterManifest = JsonSerializer.SerializeToNode(builder.BuildCluster())!.AsObject();
        var metadata = clusterManifest["metadata"]!.AsObject();
        var spec = clusterManifest["spec"]!.AsObject();
        var storage = spec["storage"]!.AsObject();
        var secret = spec["secret"]!.AsObject();

        Assert.Equal("Cluster", clusterManifest["kind"]!.GetValue<string>());
        Assert.Equal("cluster-1", metadata["name"]!.GetValue<string>());
        Assert.Equal("pole-data", metadata["namespace"]!.GetValue<string>());
        Assert.Equal(2, spec["instances"]!.GetValue<int>());
        var bootstrap = spec["bootstrap"]!.AsObject()["initdb"]!.AsObject();
        var bootstrapSecret = bootstrap["secret"]!.AsObject();

        Assert.Equal("20Gi", storage["size"]!.GetValue<string>());
        Assert.Equal("cluster-1-secret", secret["name"]!.GetValue<string>());
        Assert.Equal("cluster-1", bootstrap["database"]!.GetValue<string>());
        Assert.Equal("app_user", bootstrap["owner"]!.GetValue<string>());
        Assert.Equal("cluster-1-secret", bootstrapSecret["name"]!.GetValue<string>());
    }

    [Fact]
    public void BuildSecret_UsesProvidedCredentials()
    {
        var builder = CreateSubject();

        var secret = builder.BuildSecret("app_user", "super-secret");

        Assert.Equal("v1", secret.ApiVersion);
        Assert.Equal("Secret", secret.Kind);
        Assert.Equal("cluster-1-secret", secret.Metadata.Name);
        Assert.Equal("pole-data", secret.Metadata.NamespaceProperty);
        Assert.Equal("kubernetes.io/basic-auth", secret.Type);
        Assert.Equal("app_user", secret.StringData["username"]);
        Assert.Equal("super-secret", secret.StringData["password"]);
    }

    private static PostgresClusterKubernetesBuilder CreateSubject()
    {
        var cluster = new PostgresCluster("cluster-1", "analytics", "rg-1", new PostgresClusterSpec(2, 20, "pole-data"));

        return new PostgresClusterKubernetesBuilder(cluster, "app_user");
    }
}
