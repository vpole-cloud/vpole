using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;
namespace Pole.Features.Request;

/// <summary>
/// Should be inherited by records without primary constructor
/// because value binding does not work with them in inherited records.
/// </summary>
public abstract record PaginatedRequest
{
    [FromQuery, DefaultValue(1), Description("The page number to retrieve (1-based).")]
    public int PageNumber { get; init; } = 1;
    [FromQuery, DefaultValue(10), Description("The number of items per page.")]
    public int PageSize { get; init; } = 10;
    public int Skip => (PageNumber - 1) * PageSize;
}

public sealed record PaginatedResponse<T>(
    IEnumerable<T> Items,
    long TotalCount,
    int PageNumber,
    int PageSize)
{
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / (PageSize == 0 ? 1 : PageSize));
    public bool HasPrevious => PageNumber > 1;
    public bool HasNext => PageNumber < TotalPages;
}
