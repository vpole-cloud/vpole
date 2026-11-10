using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Domain;

public static class GitLifecycleRemovalGraph
{
    public static bool IsAcyclic(
        IEnumerable<GitPreviewOperation> operations,
        IEnumerable<GitPreviewDependencySet> dependencies)
    {
        var destructive = operations
            .Where(item => item.Action is GitLifecycleAction.Remove or GitLifecycleAction.Replace)
            .ToDictionary(item => item.Resource);
        var graph = new Dictionary<(string Action, GitLifecycleStep Step), HashSet<(string, GitLifecycleStep)>>();
        foreach (var action in destructive.Values)
        {
            graph[(action.Id, GitLifecycleStep.BeginRemoval)] = [];
            graph[(action.Id, GitLifecycleStep.RemovalCompleted)] = [(action.Id, GitLifecycleStep.BeginRemoval)];
        }

        foreach (var set in dependencies)
        {
            if (!destructive.TryGetValue(set.Target, out var target))
                return false;
            foreach (var dependency in set.Dependencies)
            {
                if (!destructive.TryGetValue(dependency.Dependent, out var dependent))
                    return false;
                switch (dependency.Order)
                {
                    case GitLifecycleDependencyOrder.BeforeRemoval:
                        graph[(target.Id, GitLifecycleStep.BeginRemoval)]
                            .Add((dependent.Id, GitLifecycleStep.RemovalCompleted));
                        break;
                    case GitLifecycleDependencyOrder.AfterRemovalRequested:
                        graph[(dependent.Id, GitLifecycleStep.BeginRemoval)]
                            .Add((target.Id, GitLifecycleStep.BeginRemoval));
                        graph[(target.Id, GitLifecycleStep.RemovalCompleted)]
                            .Add((dependent.Id, GitLifecycleStep.RemovalCompleted));
                        break;
                    case GitLifecycleDependencyOrder.AfterDependentRemovalRequested:
                        graph[(target.Id, GitLifecycleStep.BeginRemoval)]
                            .Add((dependent.Id, GitLifecycleStep.BeginRemoval));
                        break;
                    default:
                        return false;
                }
            }
        }

        while (graph.Count > 0)
        {
            var ready = graph.Where(item => item.Value.Count == 0).Select(item => item.Key).ToArray();
            if (ready.Length == 0)
                return false;
            foreach (var item in ready)
                graph.Remove(item);
            foreach (var predecessors in graph.Values)
                predecessors.ExceptWith(ready);
        }

        return true;
    }
}
