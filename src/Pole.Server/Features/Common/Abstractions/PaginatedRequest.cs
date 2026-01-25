using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Server.Mediator;

namespace Pole.Server.Features.Common.Abstractions;

/// <summary>
/// Should be inherited by records without primary constructor
/// because value binding does not work with them in inherited records.
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract record PaginatedRequest<T> : IMediatorRequest<PaginatedResponse<T>>
{
    [FromQuery, DefaultValue(1), Description("The page number to retrieve (1-based).")]
    public int PageNumber { get; init; } = 1;
    [FromQuery, DefaultValue(10), Description("The number of items per page.")]
    public int PageSize { get; init; } = 10;
    internal int Skip => (PageNumber - 1) * PageSize;
}

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