using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Pole.Tests.E2E.GitHubStub;

public sealed class GitHubStubState
{
    private const string RepositoryName = "source-build-sample";
    private const string RepositoryFullName = "pole-e2e/source-build-sample";
    private const long RepositoryId = 9001;
    private readonly ConcurrentQueue<SanitizedRequest> _requests = new();
    private readonly IReadOnlyDictionary<string, Revision> _revisions = new Dictionary<string, Revision>(StringComparer.Ordinal)
    {
        ["v1"] = new(
            "v1",
            "1111111111111111111111111111111111111111",
            "e2e-tree-v1",
            "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.MapGet(\"/\", () => \"Pole E2E sample v1\");\napp.Run();\n"),
        ["v2"] = new(
            "v2",
            "2222222222222222222222222222222222222222",
            "e2e-tree-v2",
            "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.MapGet(\"/\", () => \"Pole E2E sample v2\");\napp.Run();\n")
    };
    private string _currentRevision = "v1";

    public Revision CurrentRevision => _revisions[Volatile.Read(ref _currentRevision)];

    public void Reset()
    {
        Interlocked.Exchange(ref _currentRevision, "v1");
        ClearRequests();
    }

    public bool SetRevision(string revision)
    {
        if (!_revisions.ContainsKey(revision))
            return false;

        Interlocked.Exchange(ref _currentRevision, revision);
        return true;
    }

    public bool TryGetRevisionByCommit(string commitSha, out Revision revision) =>
        (revision = _revisions.Values.SingleOrDefault(value =>
            string.Equals(value.CommitSha, commitSha, StringComparison.Ordinal))!) is not null;

    public bool TryGetRevisionByTree(string treeSha, out Revision revision) =>
        (revision = _revisions.Values.SingleOrDefault(value =>
            string.Equals(value.TreeSha, treeSha, StringComparison.Ordinal))!) is not null;

    public void Record(HttpRequest request) =>
        _requests.Enqueue(new SanitizedRequest(
            request.Method,
            request.Path.Value ?? string.Empty,
            request.Query.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray()));

    public IReadOnlyList<SanitizedRequest> Requests => _requests.ToArray();

    public byte[] CreateArchive(Revision revision)
    {
        var root = $"pole-e2e-source-build-{revision.CommitSha}";
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            WriteFile(tar, $"{root}/Dockerfile", SourceDockerfile);
            WriteFile(tar, $"{root}/src/Pole.E2E.Sample.csproj", ProjectFile);
            WriteFile(tar, $"{root}/src/Program.cs", revision.ProgramSource);
        }

        return archive.ToArray();
    }

    public object Repository(string baseUrl) => new
    {
        id = RepositoryId,
        name = RepositoryName,
        full_name = RepositoryFullName,
        html_url = $"{baseUrl.TrimEnd('/')}/pole-e2e/{RepositoryName}",
        @private = true,
        default_branch = "main",
        updated_at = "2026-07-11T00:00:00Z"
    };

    public object Branch(Revision revision) => new
    {
        name = "main",
        commit = new { sha = revision.CommitSha },
        @protected = true
    };

    private void ClearRequests()
    {
        while (_requests.TryDequeue(out _))
        {
        }
    }

    private static void WriteFile(TarWriter writer, string path, string content)
    {
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var entry = new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = data };
        writer.WriteEntry(entry);
    }

    private const string ProjectFile = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <AssemblyName>Pole.E2E.Sample</AssemblyName>
          </PropertyGroup>
        </Project>
        """;

    public const string SourceDockerfile = """
        FROM scratch
        COPY src/Program.cs /app/revision.txt
        """;
}

public sealed record Revision(string Name, string CommitSha, string TreeSha, string ProgramSource);

public sealed record SanitizedRequest(string Method, string Path, IReadOnlyList<string> QueryKeys);
