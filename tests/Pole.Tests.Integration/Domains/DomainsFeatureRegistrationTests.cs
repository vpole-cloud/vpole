using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Persistence;
using Pole.Features.Domains.Contracts;
using Xunit;

namespace Pole.Tests.Integration.Domains;

public sealed class DomainsFeatureRegistrationTests
{
    [Fact]
    public void Feature_registration_contains_domain_persistence_capabilities()
    {
        var services = new ServiceCollection();

        services.AddDomainsFeature();

        AssertScoped<MongoLoadTenantDomainZone>(services);
        AssertScoped<LoadTenantDomainZone>(services);
        AssertScoped<MongoLoadTenantDomainZones>(services);
        AssertScoped<LoadTenantDomainZones>(services);
        AssertScoped<MongoCreateManagedDomainZoneReservation>(services);
        AssertScoped<CreateManagedDomainZoneReservation>(services);
        AssertScoped<MongoDeleteManagedDomainZone>(services);
        AssertScoped<DeleteManagedDomainZone>(services);
        AssertScoped<GetOrCreateGeneratedDomainZoneOutcome>(services);
        AssertScoped<GetOrCreateGeneratedDomainZonesOutcome>(services);
        AssertScoped<CreateGeneratedHostnameBindingOutcome>(services);
        AssertScoped<CreateGeneratedHostnameBindingsOutcome>(services);
        AssertScoped<CreateCustomHostnameBindingOutcome>(services);
        AssertScoped<DeleteCustomHostnameBindingOutcome>(services);
        AssertHttpMapping<DomainZoneNotFound>(services);
        AssertHttpMapping<CreateManagedDomainZoneError>(services);
        AssertHttpMapping<DeleteManagedDomainZoneError>(services);
        AssertHttpMapping<CustomHostnameBindingError>(services);
        AssertHttpMapping<GeneratedDomainZoneError>(services);
        AssertHttpMapping<GeneratedHostnameBindingError>(services);
    }

    private static void AssertScoped<T>(IEnumerable<ServiceDescriptor> services)
        where T : notnull =>
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(T) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);

    private static void AssertHttpMapping<T>(IEnumerable<ServiceDescriptor> services)
        where T : notnull =>
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(HttpProblemMapping) &&
            descriptor.ImplementationInstance is HttpProblemMapping mapping &&
            mapping.MappedType == typeof(T));
}
