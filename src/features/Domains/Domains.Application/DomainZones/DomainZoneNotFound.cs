using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.HostnameBindings;
using Pole.Features.Domains.Application.Lifecycle;
using Pole.Features.Domains.Application.Mongo;
namespace Pole.Features.Domains.Application.DomainZones;

public sealed record DomainZoneNotFound(string ZoneId);
