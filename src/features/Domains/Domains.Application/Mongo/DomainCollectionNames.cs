using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application.HostnameBindings;
using Pole.Features.Domains.Application.Lifecycle;
namespace Pole.Features.Domains.Application.Mongo;

internal static class DomainCollectionNames
{
    public const string TenantDomainZones = "TenantDomainZones";
    public const string HostnameBindings = "HostnameBindings";
}
