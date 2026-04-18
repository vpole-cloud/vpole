using System.Reflection;

namespace Pole.Features.Outbox;

public sealed class OutboxEventAssemblyCatalog(IEnumerable<Assembly> assemblies)
{
    public IReadOnlyCollection<Assembly> Assemblies { get; } = assemblies
        .Where(assembly => !assembly.IsDynamic)
        .Distinct()
        .ToArray();
}
