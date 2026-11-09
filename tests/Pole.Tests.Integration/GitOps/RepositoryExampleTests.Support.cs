using Pole.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Primitives;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected CompiledSnapshot CompileExample(string example) =>
        Services.GetRequiredService<ManifestCompiler>().Compile(ReadExample(example));

    private protected static ManifestFile[] ReadExample(string example) => RepositoryExampleFiles.Read(
            $"src/features/GitOps/examples/{example}/manifests")
        .Select(file => new ManifestFile(file.Path, file.Content)).ToArray();
}
