using MongoDB.Driver;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence;

internal static class RedisPersistenceGuards
{
    public static void EnsureSameIdentity(
        Redis current,
        Redis replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(current.TenantId, replacement.TenantId, StringComparison.Ordinal) ||
            !string.Equals(current.Id, replacement.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A Redis replacement must preserve tenant and resource identity.");
        }
    }

    public static void EnsureTransaction(
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
