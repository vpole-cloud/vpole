using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace Pole.ASP.Helpers;

public abstract class HttpProblemMapping
{
    private protected HttpProblemMapping() { }

    public abstract Type MappedType { get; }

    public static HttpProblemMapping For<TError>(
        Func<TError, HttpProblem> map)
        where TError : notnull
    {
        ArgumentNullException.ThrowIfNull(map);
        return new TypedHttpProblemMapping<TError>(map);
    }

    internal abstract HttpProblem Map(object value);

    private sealed class TypedHttpProblemMapping<TError>(
        Func<TError, HttpProblem> map)
        : HttpProblemMapping
        where TError : notnull
    {
        public override Type MappedType => typeof(TError);

        internal override HttpProblem Map(object value) =>
            map((TError)value) ??
            throw new InvalidOperationException(
                $"The HTTP problem mapping for '{typeof(TError).FullName}' returned null.");
    }
}

public sealed class HttpProblemMappingSet
{
    private readonly FrozenDictionary<Type, HttpProblemMapping> mappings;

    public HttpProblemMappingSet(IEnumerable<HttpProblemMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var mappingArray = mappings.ToArray();
        if (mappingArray.Any(mapping => mapping is null))
            throw new ArgumentException("An HTTP problem mapping cannot be null.", nameof(mappings));

        var duplicate = mappingArray
            .GroupBy(mapping => mapping.MappedType)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"HTTP problem mapping for '{duplicate.Key.FullName}' is registered more than once.");
        }

        this.mappings = mappingArray.ToFrozenDictionary(
            mapping => mapping.MappedType);
    }

    public IReadOnlyCollection<Type> MappedTypes => mappings.Keys;

    internal bool TryMap(object value, out HttpProblem problem)
    {
        if (mappings.TryGetValue(value.GetType(), out var mapping))
        {
            problem = mapping.Map(value);
            return true;
        }

        problem = null!;
        return false;
    }
}

public interface IHttpProblemMapper
{
    HttpProblem Map<TError>(
        TError error,
        HttpProblemMappingSet? overrides = null)
        where TError : notnull;
}

public sealed class HttpProblemMapper(
    IEnumerable<HttpProblemMapping> mappings)
    : IHttpProblemMapper
{
    private readonly HttpProblemMappingSet defaults = new(mappings);

    public HttpProblem Map<TError>(
        TError error,
        HttpProblemMappingSet? overrides = null)
        where TError : notnull
    {
        ArgumentNullException.ThrowIfNull(error);
        var candidates = Unwrap(error);
        if (overrides is not null)
        {
            foreach (var candidate in candidates)
            {
                if (overrides.TryMap(candidate, out var problem))
                    return problem;
            }
        }

        foreach (var candidate in candidates)
        {
            if (defaults.TryMap(candidate, out var problem))
                return problem;
        }

        throw new InvalidOperationException(
            $"No HTTP problem mapping is registered for '{typeof(TError).FullName}'. " +
            $"Resolved types: {string.Join(", ", candidates.Select(candidate => candidate.GetType().FullName))}.");
    }

    private static IReadOnlyList<object> Unwrap(object value)
    {
        var candidates = new List<object>();
        var current = value;
        while (true)
        {
            candidates.Add(current);
            if (current is not IUnion { Value: { } unionValue })
                return candidates;
            current = unionValue;
        }
    }
}
