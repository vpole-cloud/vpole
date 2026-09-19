using MongoDB.Driver;
using Skrynia;

namespace Pole.Tests.Skrynia;

internal sealed class SkryniaFixture : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "skrynia", Guid.NewGuid().ToString("N"));
    private SkryniaClient _owner;

    public SkryniaFixture()
    {
        Directory.CreateDirectory(_directory);
        _owner = SkryniaClient.Open(Path.Combine(_directory, "pole.sqlite"));
    }

    public IMongoClient Client => _owner.Client;
    public IMongoDatabase Database => Client.GetDatabase("pole-database");
    public void Reopen()
    {
        _owner.Dispose();
        _owner = SkryniaClient.Open(Path.Combine(_directory, "pole.sqlite"));
    }
    public void Dispose()
    {
        _owner.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
