// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Pole.Testing;

internal static class ContainerRuntimeModuleInitializer
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize() => ContainerRuntimeBootstrap.ConfigureFromLabStateIfAvailable();
}
