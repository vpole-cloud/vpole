using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected static ManifestFile[] SecretSnapshot() =>
    [
        .. GroupSnapshot(),
        new("secret.yaml", """
            apiVersion: pole.io/v1alpha1
            kind: Secret
            metadata:
              name: drift-secret
            spec:
              resourceGroup: drift-group
              purpose: managed purpose
              keys: [API_TOKEN]
            """)
    ];
}
