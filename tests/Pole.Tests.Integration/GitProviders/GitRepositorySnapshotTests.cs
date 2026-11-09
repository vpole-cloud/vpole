using System.Collections.Immutable;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitProviders.Application;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Tenants.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitProviders;

[Collection(IntegrationTestCollection.Global3)]
public sealed class GitRepositorySnapshotTests(MongoReplicaSetFixture fixture) : IsolatedMediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task RegisteredAdapterReadsYamlOnlyRepositoryAndFeedsCurrentCompiler()
    {
        const string manifest = """
            apiVersion: pole.io/v1alpha1
            kind: ResourceGroup
            metadata:
              name: repository-group
            spec:
              description: From a YAML-only repository
            """;
        var connection = GitProviderConnectionFactory.Create(
            Data.TenantId, "production-git", GitProviderKind.GitHub,
            "42", "account", "display", GitProviderAccountType.Organization, GitProviderRepositorySelection.Selected,
            "departed-author", GitProviderConnectionFactory.DefaultOptions);
        await DbContext.GitProviderConnections.InsertOneAsync(connection);
        var client = new FakeGitHubAppClient();
        client.Snapshot = client.Snapshot with
        {
            CommitSha = new string('a', 40),
            Paths = ["manifests/group.yaml"],
            FileContents = ImmutableDictionary<string, string>.Empty.Add("manifests/group.yaml", manifest)
        };
        client.ArchiveBytes = Archive(manifest);
        using var services = new ServiceCollection().AddSingleton(Fixture.Database)
            .AddSingleton(Services.GetRequiredService<LoadTenantIdentity>())
            .AddGitProvidersFeature(new ConfigurationBuilder().Build())
            .AddSingleton<IGitHubAppClient>(client).BuildServiceProvider();
        using var scope = services.CreateScope();
        var provider = Assert.Single(scope.ServiceProvider.GetServices<IRepositorySnapshotProvider>());
        var reference = new RepositorySourceReference(Data.TenantId, connection.Id, "9001");
        var commit = Assert.IsType<RepositoryCommitResolved>(
            (await provider.ResolveCommitAsync(new(reference, "main"), default)).Value);
        var snapshot = Assert.IsType<RepositorySnapshotRead>(
            (await provider.ReadSnapshotAsync(new(reference, commit.Commit, "manifests"), default)).Value);
        var compiled = Services.GetRequiredService<ManifestCompiler>().Compile(snapshot.Files
            .Select(file => new ManifestFile(file.Path, file.Content)).ToArray());
        Assert.True(compiled.Valid, string.Join(", ", compiled.Diagnostics));
        Assert.Single(compiled.Declarations);
        Assert.Equal(0, await DbContext.SourceRepositories.CountDocumentsAsync(_ => true));

        await DbContext.GitProviderConnections.DeleteOneAsync(item => item.Id == connection.Id);
        Assert.IsType<RepositoryAccessDenied>(
            (await provider.ReadSnapshotAsync(new(reference, commit.Commit, "manifests"), default)).Value);
    }

    private static byte[] Archive(string manifest)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "repository"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "repository/manifests/group.yaml")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(manifest))
            });
        }

        return compressed.ToArray();
    }
}
