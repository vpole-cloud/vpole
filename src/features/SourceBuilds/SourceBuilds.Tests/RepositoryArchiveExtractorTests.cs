using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Pole.Features.SourceBuilds.Application.Execution;
using Xunit;

namespace Pole.Features.SourceBuilds.Tests;

public sealed class RepositoryArchiveExtractorTests
{
    [Fact]
    public async Task Extract_StripsGitHubRootAndWritesRegularFiles()
    {
        await using var archive = CreateArchive(
            ("owner-repo-abc/src/App.csproj", "<Project />", TarEntryType.RegularFile),
            ("owner-repo-abc/README.md", "readme", TarEntryType.RegularFile));
        using var destination = new TemporaryDirectory();
        var extractor = new RepositoryArchiveExtractor();

        var result = await extractor.InvokeAsync(archive, destination.Path, 1024, 10);

        Assert.IsType<SourceBuildRepositoryArchiveExtracted>(result.Value);
        Assert.Equal("<Project />", await File.ReadAllTextAsync(
            System.IO.Path.Combine(destination.Path, "src", "App.csproj")));
    }

    [Theory]
    [InlineData("root/../../escape.txt", TarEntryType.RegularFile)]
    [InlineData("root/link", TarEntryType.SymbolicLink)]
    public async Task Extract_RejectsTraversalAndLinks(string entryName, TarEntryType entryType)
    {
        await using var archive = CreateArchive((entryName, "payload", entryType));
        using var destination = new TemporaryDirectory();
        var extractor = new RepositoryArchiveExtractor();

        var result = await extractor.InvokeAsync(archive, destination.Path, 1024, 10);

        Assert.IsType<SourceBuildRepositoryArchiveInvalid>(result.Value);
    }

    [Fact]
    public async Task Extract_RejectsConfiguredSizeLimit()
    {
        await using var archive = CreateArchive(
            ("root/large.txt", new string('x', 100), TarEntryType.RegularFile));
        using var destination = new TemporaryDirectory();
        var extractor = new RepositoryArchiveExtractor();

        var result = await extractor.InvokeAsync(archive, destination.Path, 10, 10);

        Assert.IsType<SourceBuildRepositoryArchiveInvalid>(result.Value);
    }

    private static MemoryStream CreateArchive(
        params (string Name, string Content, TarEntryType EntryType)[] entries)
    {
        var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var (name, content, entryType) in entries)
            {
                var entry = new PaxTarEntry(entryType, name);
                if (entryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                if (entryType == TarEntryType.SymbolicLink)
                    entry.LinkName = "../target";
                writer.WriteEntry(entry);
            }
        }

        archive.Position = 0;
        return archive;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pole-archive-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
