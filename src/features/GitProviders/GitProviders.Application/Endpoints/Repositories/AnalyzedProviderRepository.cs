using Pole.Features.GitProviders.Application.Analysis;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Endpoints;

internal sealed record AnalyzedProviderRepository(
    GitProviderConnection Connection,
    ProviderRepository Repository,
    SourceRepositoryAnalysis Analysis);
