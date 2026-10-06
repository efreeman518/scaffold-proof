using System.Linq.Expressions;
using EF.Common.Contracts;
using EF.Data.Contracts;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>Ordering helpers shared by the search repositories.</summary>
internal static class SearchOrderingExtensions
{
    /// <summary>
    /// Applies the request's sorts, then breaks ties on the id in the direction of the LAST sort, so a descending
    /// sort is a descending total order. The tie-break decides ties deterministically, not by recency: SQL Server
    /// orders uniqueidentifier by its last bytes, and two UUIDv7 ids minted in the same millisecond carry random
    /// bits there. Callers supply their own default order when <paramref name="sorts"/> is empty.
    /// </summary>
    public static IOrderedQueryable<T> OrderByWithIdTieBreak<T, TId>(
        this IQueryable<T> query, IEnumerable<Sort> sorts, Expression<Func<T, TId>> id)
    {
        ArgumentNullException.ThrowIfNull(sorts);
        var list = sorts as IReadOnlyList<Sort> ?? [.. sorts]; // enumerate once
        if (list.Count == 0)
            throw new ArgumentException("At least one sort is required; supply a default order for an empty list.", nameof(sorts));

        var ordered = (IOrderedQueryable<T>)query.OrderBy(list);
        return list[^1].SortOrder == SortOrder.Descending
            ? ordered.ThenByDescending(id)
            : ordered.ThenBy(id);
    }
}
