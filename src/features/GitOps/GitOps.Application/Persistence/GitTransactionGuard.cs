using MongoDB.Driver;

namespace Pole.Features.GitOps.Application.Persistence;

internal static class GitTransactionGuard
{
    public static void Require(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Git ownership requires the resource transaction.");
    }
}
