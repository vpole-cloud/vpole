// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.ResourceGroups.Contracts;

public static class ResourceGroupVersionKeys
{
    public static string Revision(string id) => $"ResourceGroup/{id}";

    public static string IsolationScopeRevision(string id) => $"IsolationScope/{id}";

    public static string RegistrationReferenceFence(string id) => $"ResourceRegistration/{id}";
}
