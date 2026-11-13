using Microsoft.FSharp.Core;
using Pole.Features.Backups.Contracts;
using Pole.Features.Postgres.Application.Backups;
using Pole.Features.Postgres.Application.Clusters;
using Pole.Features.Postgres.Application.Creation;
using Pole.Features.Postgres.Domain;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresBackupSourceTests
{
    [Fact]
    public void Creation_authority_does_not_print_the_administrator_password()
    {
        var authority = PostgresCreationAuthority.NewNewCluster("sensitive-owner-password");
        Assert.DoesNotContain("sensitive-owner-password", authority.ToString());
    }

    [Fact]
    public async Task Missing_and_unsupported_sources_are_exact_typed_outcomes()
    {
        var reads = 0;
        LoadPostgresClusterById load = (
            _, _,
            _) =>
        {
            reads++;
            return Task.FromResult<FSharpOption<PostgresCluster>>(null!);
        };
        IBackupProvider provider = new PostgresBackupProvider(null!, load, null!, null!, null!, null!);
        Assert.IsType<BackupSourceUnsupported>(
            (await provider.DescribeAsync(new("tenant", "other/database", "source"), default)).Value);
        Assert.Equal(0, reads);
        Assert.IsType<BackupSourceUnavailable>(
            (await provider.DescribeAsync(new("tenant", "pole/postgresql", "missing"), default)).Value);
        Assert.Equal(1, reads);
        Assert.IsType<BackupRetentionInvalid>(provider.ValidateSchedule(new("0 2 * * *", false, 0)).Value);
        Assert.IsType<BackupScheduleAccepted>(provider.ValidateSchedule(new("0 2 * * *", false, 1)).Value);
    }

    [Fact]
    public async Task Describe_preserves_cancellation_instead_of_turning_it_into_a_business_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        LoadPostgresClusterById load = (
            _, _,
            token) => Task.FromCanceled<FSharpOption<PostgresCluster>>(token);
        IBackupProvider provider = new PostgresBackupProvider(null!, load, null!, null!, null!, null!);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DescribeAsync(
            new("tenant", "pole/postgresql", "source"),
            cancellation.Token));
    }
}
