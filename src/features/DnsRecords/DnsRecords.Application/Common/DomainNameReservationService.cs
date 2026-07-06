using MongoDB.Driver;
using Pole.Features.DnsRecords.Contracts;
using Pole.Features.DnsRecords.Domain;
using Pole.Features.Shared.Models;
using Pole.ResultType;

namespace Pole.Features.DnsRecords.Application.Common;

public sealed class DomainNameReservationService(
    IDnsRecordDbContext dbContext)
    : IDomainNameReservationService
{
    private static readonly TimeSpan PendingReservationTtl = TimeSpan.FromDays(1);

    public async Task<Result<DnsRecordShortInfo>> ReserveAsync(
        ReserveDomainNameRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.DnsRecords
            .Find(record => record.Domain == request.DomainName)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
            return Result.Conflict($"Domain name '{request.DomainName}' is already in use.");

        var now = DateTime.UtcNow;
        var dnsRecord = DnsRecordFactory.Create(
            name: request.DomainName,
            tenantId: request.TenantId,
            resourceType: request.ResourceType,
            resourceId: request.ResourceId,
            resourceName: request.ResourceName,
            groupId: request.ResourceGroupId,
            options: DnsRecordFactory.DefaultCreateOptions with
            {
                Status = new Pending(now, now.Add(PendingReservationTtl))
            });

        try
        {
            await dbContext.DnsRecords.InsertOneAsync(dnsRecord, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Result.Conflict($"Domain name '{request.DomainName}' is already in use.");
        }

        return new DnsRecordShortInfo(dnsRecord.Id, dnsRecord.Domain);
    }

    public async Task<Result> CommitAsync(
        CommitDomainNameRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<DnsRecord>.Filter.And(
            Builders<DnsRecord>.Filter.Eq(record => record.Id, request.DnsRecordId),
            Builders<DnsRecord>.Filter.Eq(record => record.TenantId, request.TenantId),
            Builders<DnsRecord>.Filter.Eq(record => record.Resource.Id, request.ResourceId));

        var dnsRecord = await dbContext.DnsRecords
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken);

        if (dnsRecord is null)
            return Result.NotFound($"Domain name reservation with ID '{request.DnsRecordId}' not found.");

        if (dnsRecord.Status is Committed)
            return Result.Success();

        var update = Builders<DnsRecord>.Update
            .Set(record => record.Status, new Committed(DateTime.UtcNow));

        await dbContext.DnsRecords.UpdateOneAsync(
            filter,
            update,
            cancellationToken: cancellationToken);

        return Result.Success();
    }

    public async Task<Result> ReleaseAsync(
        ReleaseDomainNameRequest request,
        CancellationToken cancellationToken = default)
    {
        await dbContext.DnsRecords.DeleteOneAsync(
            record => record.Id == request.DnsRecordId && record.TenantId == request.TenantId,
            cancellationToken);

        return Result.Success();
    }
}

