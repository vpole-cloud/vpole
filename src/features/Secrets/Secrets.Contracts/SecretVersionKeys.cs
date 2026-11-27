// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Secrets.Contracts;

public static class SecretVersionKeys
{
    public static string Revision(string id) => $"Secret/{id}";

    public static string ReferenceFence(string id) => $"{Revision(id)}/ReferenceFence";

    public static string ValueVersion(string id) => $"{Revision(id)}/ValueVersion";
}
