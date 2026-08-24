using MongoDB.Driver;

namespace Pole.Features.Domains.Application.Persistence;

internal static class DomainTransactions
{
    public static void EnsureActive(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Domain mutations must run inside the caller-owned MongoDB transaction.");
        }
    }
}
