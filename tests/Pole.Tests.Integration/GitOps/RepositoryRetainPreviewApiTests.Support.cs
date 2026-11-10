using Pole.Features.GitOps.Compiler;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class RepositorySourceApiTestBase
{
    private protected static ManifestFile GroupManifest(string name) => new(
        $"{name}.yaml",
        $$"""
          apiVersion: pole.io/v1alpha1
          kind: ResourceGroup
          metadata: {name: {{name}}}
          spec: {}
          """);
}
