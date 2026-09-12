using System.Runtime.CompilerServices;

namespace Pole.Testing;

internal static class ContainerRuntimeModuleInitializer
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize() => ContainerRuntimeBootstrap.ConfigureFromLabStateIfAvailable();
}
