using MongoDB.Driver;
using Pole.Features.Request;

namespace Pole.Database;

public static class PaginatedRequestExtensions
{
    public static IQueryable<TEntity>
        ApplyPagination<TEntity>(this IQueryable<TEntity> query, PaginatedRequest request) =>
        query.Skip(request.Skip).Take(request.PageSize);

    public static IFindFluent<T, T> ApplyPagination<T>(this IFindFluent<T, T> findFluent, PaginatedRequest request) =>
        findFluent.ApplyPagination(request.PageNumber, request.PageSize);

    public static IFindFluent<T, T> ApplyPagination<T>(
        this IFindFluent<T, T> findFluent,
        int pageNumber,
        int pageSize) => findFluent.Skip((pageNumber - 1) * pageSize).Limit(pageSize);
}
