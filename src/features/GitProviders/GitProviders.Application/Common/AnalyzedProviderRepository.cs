// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Common;

public sealed record AnalyzedProviderRepository(
    GitProviderConnection Connection,
    ProviderRepository Repository,
    SourceRepositoryAnalysis Analysis
);
