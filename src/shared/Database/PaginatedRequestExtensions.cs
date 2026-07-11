using MongoDB.Driver;
using Pole.Features.Request;

namespace Pole.Database;

public static class PaginatedRequestExtensions
{
    public static IQueryable<TEntity> ApplyPagination<TEntity, TRequest>(this IQueryable<TEntity> query, PaginatedRequest<TRequest> request)
    {
        return query
            .Skip(request.Skip)
            .Take(request.PageSize);
    }
    
    public static IFindFluent<T, T> ApplyPagination<T, TRequest>(this IFindFluent<T, T> findFluent, PaginatedRequest<TRequest> request)
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
