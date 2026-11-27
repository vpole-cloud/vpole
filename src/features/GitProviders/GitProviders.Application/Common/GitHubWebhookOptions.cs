// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitProviders.Application.Common;

public sealed class GitHubWebhookOptions
{
    public const string SectionName = "GitHubWebhook";
    public const int MaximumBodyBytes = 25 * 1024 * 1024;
    public string Secret { get; init; } = "";
}
