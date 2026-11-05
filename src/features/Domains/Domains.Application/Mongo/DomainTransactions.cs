using MongoDB.Driver;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application.HostnameBindings;
using Pole.Features.Domains.Application.Lifecycle;

namespace Pole.Features.Domains.Application.Mongo;

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
