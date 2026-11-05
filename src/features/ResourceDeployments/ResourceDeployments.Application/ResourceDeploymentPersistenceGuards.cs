using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application.Mongo;

namespace Pole.Features.ResourceDeployments.Application;

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
