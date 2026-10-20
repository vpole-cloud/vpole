using System.Diagnostics;

namespace Pole.Tests.E2E.Infrastructure;

internal enum HttpReadOutcome
{
    Pending,
    Succeeded,
    Rejected,
    TransportFailed,
    Cancelled
}

internal sealed class HttpReadJournal<TKey> where TKey : notnull
{
    internal sealed record Read(
        long StartedAt,
        string Path,
        HttpReadOutcome Outcome,
        int? Status = null,
        long Document = 0);

    private readonly object sync = new();
    private readonly Dictionary<TKey, int> active = new();
    private readonly List<Read> reads = [];
    private long document;

    public void Navigate()
    {
        lock (sync)
            document++;
    }

    public IReadOnlyList<Read> Snapshot()
    {
        lock (sync)
            return reads.ToArray();
    }

    public void Start(TKey key, string path, long startedAt)
    {
        lock (sync)
        {
            active[key] = reads.Count;
            reads.Add(new Read(startedAt, path, HttpReadOutcome.Pending, Document: document));
        }
    }

    public void Response(TKey key, int status)
    {
        lock (sync)
            if (active.TryGetValue(key, out var index))
                reads[index] = reads[index] with { Status = status };
    }

    public void Finish(TKey key, bool failed = false, bool cancelled = false)
    {
        lock (sync)
        {
            if (!active.Remove(key, out var index))
                return;
            var read = reads[index];
            reads[index] = read with
            {
                Outcome = cancelled ? HttpReadOutcome.Cancelled : failed || read.Status is null
                    ? HttpReadOutcome.TransportFailed : read.Status is >= 200 and < 300
                        ? HttpReadOutcome.Succeeded : HttpReadOutcome.Rejected
            };
        }
    }

    public bool Quiet(Func<string, bool> matches, TimeSpan duration)
    {
        lock (sync)
        {
            var latest = reads.Where(read => read.Document == document && matches(read.Path))
                .GroupBy(read => read.Path)
                .Select(group => group.Last()).ToArray();
            return latest.Length > 0 && latest.All(read => read.Outcome == HttpReadOutcome.Succeeded &&
                Stopwatch.GetElapsedTime(read.StartedAt) >= duration) &&
                !active.Values.Any(index => reads[index].Document == document && matches(reads[index].Path));
        }
    }
}
