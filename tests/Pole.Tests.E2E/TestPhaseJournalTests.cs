using System.Text.Json;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class TestPhaseJournalTests
{
    [Fact]
    public void Records_only_hashed_identifiers_and_typed_phase_metadata()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new TestPhaseJournal("private-scenario-canary", directory);
            Assert.True(journal.Record(TestPhase.RequestStarted, "private-resource-canary",
                "private-uid-canary", "private-container-canary", request: 1, revision: 7, generation: 2));
            Assert.True(journal.Record(TestPhase.ResponseReceived, status: 200, request: 1));
            var output = File.ReadAllText(journal.Path);
            Assert.False(output.Contains("private-", StringComparison.Ordinal), "Identifiers must never be recorded as plaintext.");
            var records = File.ReadAllLines(journal.Path).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Equal(3, records.Length);
                Assert.Equal("Started", records[0].RootElement.GetProperty("phase").GetString());
                Assert.Equal(64, records[1].RootElement.GetProperty("resource").GetString()!.Length);
                Assert.Equal(200, records[2].RootElement.GetProperty("status").GetInt32());
                Assert.True(records[2].RootElement.GetProperty("elapsedMs").GetInt64() >=
                            records[0].RootElement.GetProperty("elapsedMs").GetInt64());
            }
            finally
            {
                foreach (var record in records) record.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Bounds_records_and_does_not_replace_a_scenario_failure_when_storage_disappears()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new TestPhaseJournal("bounded", directory);
            for (var index = 0; index < 1100; index++) journal.Record(TestPhase.StreamReading);
            var lines = File.ReadAllLines(journal.Path);
            Assert.Equal(1024, lines.Length);
            using var last = JsonDocument.Parse(lines[^1]);
            Assert.Equal("Truncated", last.RootElement.GetProperty("phase").GetString());
            var unavailable = new TestPhaseJournal("unavailable", directory);
            Directory.Delete(directory, recursive: true);
            Assert.False(unavailable.Record(TestPhase.Failed));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
