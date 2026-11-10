using Pole.Features.Realtime.Contracts;

namespace Pole.Features.Realtime.Application;

public sealed class RealtimeTopicRegistry
{
    private readonly HashSet<string> _enabled;
    private readonly IReadOnlyDictionary<string, RealtimeTopic> _topics;

    public RealtimeTopicRegistry(IEnumerable<RealtimeTopic> topics, RealtimeOptions options)
    {
        var registered = topics.ToArray();
        if (registered.Any(topic => string.IsNullOrWhiteSpace(topic.Name) || topic.Name.Length > 100) || registered
                .GroupBy(topic => topic.Name, StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
            throw new InvalidOperationException("Realtime topics require unique nonempty identities.");
        _topics = registered.ToDictionary(topic => topic.Name, StringComparer.Ordinal);
        _enabled = options.EnabledTopics.ToHashSet(StringComparer.Ordinal);
        if (options.Enabled && _enabled.Any(topic => !_topics.ContainsKey(topic)))
            throw new InvalidOperationException("Realtime:EnabledTopics contains an unregistered topic.");
    }

    public RealtimeTopic? Find(string name) => _topics.GetValueOrDefault(name);
    public bool IsEnabled(string name) => _enabled.Count == 0 || _enabled.Contains(name);
}
