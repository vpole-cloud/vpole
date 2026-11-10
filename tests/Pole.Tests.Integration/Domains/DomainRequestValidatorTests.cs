using MongoDB.Bson;
using Pole.Features.Domains.Application.Endpoints;
using Pole.Features.Domains.Domain;
using Xunit;

namespace Pole.Tests.Integration.Domains;

public sealed class DomainRequestValidatorTests
{
    [Fact]
    public async Task Create_requires_body_tenant_target_and_unreserved_dns_label()
    {
        var validator = new CreateDomainZoneRequestValidator();

        var missingBody = await validator.ValidateAsync(new CreateDomainZoneRequest(null!, ""));
        Assert.Contains(missingBody.Errors, error => error.PropertyName == "Body");
        Assert.Contains(missingBody.Errors, error => error.PropertyName == "TenantId");

        var reserved = await validator.ValidateAsync(
            new CreateDomainZoneRequest(
                new CreateDomainZoneBody(
                    ObjectId.GenerateNewId().ToString(),
                    $"{TenantDomainZoneLabelPolicy.GeneratedPrefix}acme-12345678",
                    "zone-" + ObjectId.GenerateNewId()),
                ObjectId.GenerateNewId().ToString()));
        Assert.Contains(reserved.Errors, error => error.PropertyName == "Body.Label");

        var invalidLabel = await validator.ValidateAsync(
            new CreateDomainZoneRequest(
                new CreateDomainZoneBody(
                    ObjectId.GenerateNewId().ToString(),
                    "invalid.label",
                    "zone-" + ObjectId.GenerateNewId()),
                ObjectId.GenerateNewId().ToString()));
        Assert.Contains(invalidLabel.Errors, error => error.PropertyName == "Body.Label");
    }

    [Fact]
    public async Task Read_and_delete_requests_require_tenant_and_object_ids()
    {
        var get = await new GetDomainZoneRequestValidator().ValidateAsync(
            new GetDomainZoneRequest("invalid", "invalid"));
        var delete = await new DeleteDomainZoneRequestValidator().ValidateAsync(
            new DeleteDomainZoneRequest("invalid", "invalid"));
        var list = await new GetDomainZonesRequestValidator().ValidateAsync(
            new GetDomainZonesRequest("invalid", "invalid"));

        Assert.Equal(2, get.Errors.Count);
        Assert.Equal(2, delete.Errors.Count);
        Assert.Equal(2, list.Errors.Count);
        Assert.All(
            new[] { get, delete, list },
            result => Assert.Contains(
                result.Errors,
                error => error.PropertyName == nameof(GetDomainZoneRequest.TenantId)));
    }
}
