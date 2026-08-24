using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Common;

public sealed record AnalyzedProviderRepository(
    GitProviderConnection Connection,
    ProviderRepository Repository,
    SourceRepositoryAnalysis Analysis);
