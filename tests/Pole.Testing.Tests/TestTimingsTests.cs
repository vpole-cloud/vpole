using System.Text.Json;
using Pole.Testing;
using Xunit;

namespace Pole.Testing.Tests;

public sealed class TestTimingsTests
{
    [Fact]
    public async Task Concurrent_async_scopes_preserve_complete_records_and_instance_identity()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pole-timing-{Guid.NewGuid():N}");
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
            {
                var timing = new TestTimings(GetType(), directory: directory);
                using (timing.Measure("seed.base"))
                    await Task.Yield();
                using (timing.Measure("api.host.dispose"))
                    await Task.Yield();
            }));
            var records = File.ReadLines(Assert.Single(Directory.GetFiles(directory)))
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Equal(64, records.Length);
                var instances = records.GroupBy(record => record.RootElement.GetProperty("instance").GetString());
                Assert.Equal(32, instances.Count());
                Assert.All(instances, instance =>
                {
                    Assert.Equal(2, instance.Count());
                    Assert.Equal(["api.host.dispose", "seed.base"], instance
                        .Select(record => record.RootElement.GetProperty("phase").GetString()).Order());
                });
                Assert.All(records, record =>
                {
                    var root = record.RootElement;
                    Assert.Equal(GetType().FullName, root.GetProperty("owner").GetString());
                    Assert.Equal("test", root.GetProperty("kind").GetString());
                    Assert.True(root.GetProperty("elapsedMs").GetDouble() >= 0);
                    Assert.True(root.GetProperty("finishedUtc").GetDateTimeOffset() >=
                                root.GetProperty("startedUtc").GetDateTimeOffset());
                });
            }
            finally
            {
                foreach (var record in records) record.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Diagnostic_storage_failure_preserves_the_scenario_exception()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pole-timing-{Guid.NewGuid():N}");
        var timing = new TestTimings(GetType(), directory: directory);
        Directory.Delete(directory, recursive: true);
        var original = new InvalidOperationException("private-body-canary");
        var observed = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (timing.Measure("seed.base"))
                throw original;
        }));
        Assert.Same(original, observed);
        Assert.False(Directory.Exists(directory));
    }
}
