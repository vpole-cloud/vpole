using System.Collections.Immutable;

namespace Pole.Domain.Models;

// TODO: remove maybe
public sealed record Tags(
    ImmutableHashSet<string> Value
        );