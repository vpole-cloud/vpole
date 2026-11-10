using System.Collections.Immutable;

namespace Pole.Features.GitOps.Domain;

public static class GitLifecyclePreconditionAdvancement
{
    public static ImmutableArray<GitPreviewOperation> Advance(
        ImmutableArray<GitPreviewOperation> operations,
        IEnumerable<GitLifecycleEffect> effects)
    {
        var current = operations;
        foreach (var effect in effects.OrderBy(item => item.Sequence))
        {
            var own = current.SingleOrDefault(item => item.Id == effect.ActionId) ??
                throw new InvalidOperationException("A receipt cannot authorize an unreviewed action.");
            if ((effect.Subject != own.Resource &&
                    (effect.Subject != own.Replacement || effect.Step != GitLifecycleStep.Apply)) ||
                (effect.Postcondition is { } postcondition && postcondition.Resource != effect.Subject))
                throw new InvalidOperationException("A receipt cannot change an unreviewed resource identity.");

            var versions = effect.RelatedVersions
                .Concat(effect.Postcondition?.Versions ?? [])
                .GroupBy(item => item.DocumentId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            current = current.Select(operation =>
                    operation.Id == effect.ActionId && effect.Subject == operation.Resource
                        ?
                        operation with { Expected = effect.Postcondition }
                        : operation.Expected is { } expected
                            ? operation with
                            {
                                Expected = expected with
                                {
                                    Versions = expected.Versions
                                        .Select(version => versions.GetValueOrDefault(version.DocumentId) ?? version)
                                        .ToImmutableArray(),
                                },
                            }
                            : operation)
                .ToImmutableArray();
        }

        return current;
    }
}
