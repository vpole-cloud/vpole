using MongoDB.Driver;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Configuration;

internal static class WebServicePersistenceGuards
{
    public static void EnsureSameIdentity(WebService current, WebService replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(current.TenantId, replacement.TenantId, StringComparison.Ordinal) ||
            !string.Equals(current.Id, replacement.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A web service replacement must preserve tenant and resource identity.");
        }
    }

    public static void EnsureTransaction(IClientSessionHandle session, string operation)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
            throw new InvalidOperationException($"{operation} must run inside a transaction.");
    }
}
