using MongoDB.Driver;
using Pole.Features.Request;

namespace Pole.Database;

public static class PaginatedRequestExtensions
{
    public static IQueryable<TEntity> ApplyPagination<TEntity>(this IQueryable<TEntity> query, PaginatedRequest request)
    {
        return query
            .Skip(request.Skip)
            .Take(request.PageSize);
    }
    
    public static IFindFluent<T, T> ApplyPagination<T>(this IFindFluent<T, T> findFluent, PaginatedRequest request)
    {
        return findFluent.ApplyPagination(request.PageNumber, request.PageSize);
    }

    public static IFindFluent<T, T> ApplyPagination<T>(this IFindFluent<T, T> findFluent, int pageNumber, int pageSize)
    {
        return findFluent
            .Skip((pageNumber - 1) * pageSize)
            .Limit(pageSize);
    }
}
