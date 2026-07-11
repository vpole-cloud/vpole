using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PoleE2ECollection : ICollectionFixture<PoleE2EFixture>
{
    public const string Name = "Pole Aspire end-to-end";
}
