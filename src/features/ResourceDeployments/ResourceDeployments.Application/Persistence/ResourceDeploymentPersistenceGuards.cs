using MongoDB.Driver;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

internal static class ResourceDeploymentPersistenceGuards
{
    public static void EnsureActiveTransaction(
        IClientSessionHandle session,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                $"{operation} must run inside the logical resource transaction.");
        }
    }
}
