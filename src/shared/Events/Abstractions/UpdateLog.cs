using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Pole.Features.Events.Abstractions;

public sealed record UpdateLog<T>(
    T Previous,
    T New,
    bool AreEqual
) : IUpdateLog<T>
{
    public static UpdateLog<T> Of(T previous, T @new,
        IEqualityComparer<T>? comparer = null)
        => new UpdateLog<T>(previous, @new,
            comparer?.Equals(previous, @new) ??
            DeepEqualityComparer.AreEqual(previous, @new));
}

public interface IUpdateLog<out T>
{
    public T Previous { get; }
    public T New { get; }
    public bool AreEqual { get; }
}

internal static class DeepEqualityComparer
{
    public static bool AreEqual<T>(T previous, T @new) =>
        AreEqual(previous, @new, new HashSet<ReferencePair>());

    private static bool AreEqual(object? previous, object? @new, HashSet<ReferencePair> visited)
    {
        if (ReferenceEquals(previous, @new)) return true;
        if (previous is null || @new is null) return false;

        var type = previous.GetType();
        if (type != @new.GetType()) return false;
        if (IsScalar(type)) return previous.Equals(@new);

        if (!type.IsValueType && !visited.Add(new ReferencePair(previous, @new)))
            return true;

        if (previous is IDictionary previousDictionary && @new is IDictionary newDictionary)
            return DictionaryEquals(previousDictionary, newDictionary, visited);

        if (IsSet(type))
            return SetEquals((IEnumerable)previous, (IEnumerable)@new, visited);

        if (previous is IEnumerable previousEnumerable && @new is IEnumerable newEnumerable)
            return SequenceEquals(previousEnumerable, newEnumerable, visited);

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead) continue;
            if (!AreEqual(property.GetValue(previous), property.GetValue(@new), visited)) return false;
        }

        return true;
    }

    private static bool DictionaryEquals(IDictionary previous, IDictionary @new, HashSet<ReferencePair> visited)
    {
        if (previous.Count != @new.Count) return false;

        foreach (DictionaryEntry entry in previous)
        {
            if (!@new.Contains(entry.Key)) return false;
            if (!AreEqual(entry.Value, @new[entry.Key], visited)) return false;
        }

        return true;
    }

    private static bool SetEquals(IEnumerable previous, IEnumerable @new, HashSet<ReferencePair> visited)
    {
        var remaining = @new.Cast<object?>().ToList();
        foreach (var previousItem in previous.Cast<object?>())
        {
            var index = remaining.FindIndex(newItem => AreEqual(previousItem, newItem, visited));
            if (index < 0) return false;
            remaining.RemoveAt(index);
        }

        return remaining.Count == 0;
    }

    private static bool SequenceEquals(IEnumerable previous, IEnumerable @new, HashSet<ReferencePair> visited)
    {
        var previousEnumerator = previous.GetEnumerator();
        var newEnumerator = @new.GetEnumerator();
        try
        {
            while (true)
            {
                var hasPrevious = previousEnumerator.MoveNext();
                var hasNew = newEnumerator.MoveNext();
                if (hasPrevious != hasNew) return false;
                if (!hasPrevious) return true;
                if (!AreEqual(previousEnumerator.Current, newEnumerator.Current, visited)) return false;
            }
        }
        finally
        {
            (previousEnumerator as IDisposable)?.Dispose();
            (newEnumerator as IDisposable)?.Dispose();
        }
    }

    private static bool IsScalar(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type == typeof(string)
        || type == typeof(decimal)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(TimeSpan)
        || type == typeof(Guid);

    private static bool IsSet(Type type) =>
        type.GetInterfaces().Any(i =>
            i.IsGenericType &&
            (i.GetGenericTypeDefinition() == typeof(ISet<>) ||
             i.GetGenericTypeDefinition().FullName == "System.Collections.Immutable.IImmutableSet`1"));

    private readonly record struct ReferencePair(object Previous, object New)
    {
        public bool Equals(ReferencePair other) =>
            ReferenceEquals(Previous, other.Previous) && ReferenceEquals(New, other.New);

        public override int GetHashCode() =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(Previous), RuntimeHelpers.GetHashCode(New));
    }
}
