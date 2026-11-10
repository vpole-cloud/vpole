using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Compiler;

public static class DeclarationPlan
{
    public static bool TryOrder(
        IReadOnlyList<CompiledDeclaration> declarations,
        out IReadOnlyList<(CompiledDeclaration Declaration, DeclarationPhase Phase)> ordered)
    {
        var pending = declarations.SelectMany(item => item.Steps.Select(step => (Declaration: item, Step: step)))
            .OrderBy(item => item.Step.Phase)
            .ThenBy(item => item.Declaration.Address.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Declaration.Address.Name, StringComparer.Ordinal)
            .ToList();
        var all = pending.Select(item => (item.Declaration.Address, item.Step.Phase)).ToHashSet();
        var complete = new HashSet<(ResourceAddress, DeclarationPhase)>();
        var result = new List<(CompiledDeclaration, DeclarationPhase)>();
        while (pending.Count > 0)
        {
            var next = pending.FindIndex(item => item.Step.Dependencies.All(dependency =>
                !all.Contains((dependency.Resource, dependency.Phase)) ||
                complete.Contains((dependency.Resource, dependency.Phase))));

            if (next < 0)
            {
                ordered = [];
                return false;
            }

            var item = pending[next];
            pending.RemoveAt(next);
            complete.Add((item.Declaration.Address, item.Step.Phase));
            result.Add((item.Declaration, item.Step.Phase));
        }

        ordered = result;
        return true;
    }
}
