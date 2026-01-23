using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Server.Mediator;

namespace Pole.Server.Features.Common.Abstractions;

public abstract record PaginatedRequest<T>(
    [FromQuery] int PageNumber = 1, 
    [FromQuery] int PageSize = 10
    ) : IMediatorRequest<PaginatedResponse<T>>
{
    public int Skip => (PageNumber - 1) * PageSize;
}

public static class PaginatedRequestExtensions
{
    public static IQueryable<T> ApplyPagination<T>(this IQueryable<T> query, PaginatedRequest<T> request)
    {
        return query
            .Skip(request.Skip)
            .Take(request.PageSize);
    }
    
    public static IFindFluent<T, T> ApplyPagination<T, TRequest>(this IFindFluent<T, T> findFluent, PaginatedRequest<TRequest> request)
    {
        return findFluent
            .Skip(request.Skip)
            .Limit(request.PageSize);
    }
}

public sealed record PaginatedResponse<T>(IEnumerable<T> Items, long TotalCount, int PageNumber, int PageSize)
{
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / (PageSize == 0 ? 1 : PageSize));
    public bool HasPrevious => PageNumber > 1;
    public bool HasNext => PageNumber < TotalPages;
}