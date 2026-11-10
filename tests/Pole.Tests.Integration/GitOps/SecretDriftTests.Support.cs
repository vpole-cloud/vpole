using Pole.Features.GitOps.Compiler;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected static ManifestFile[] SecretSnapshot() =>
    [
        .. GroupSnapshot(),
        new(
            "secret.yaml",
            """
            apiVersion: pole.io/v1alpha1
            kind: Secret
            metadata:
              name: drift-secret
            spec:
              resourceGroup: drift-group
              purpose: managed purpose
              keys: [API_TOKEN]
            """),
    ];
}
