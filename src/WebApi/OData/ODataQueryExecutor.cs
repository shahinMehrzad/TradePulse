using Application.Interfaces;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Validator;
using Microsoft.OData;

namespace WebApi.OData;

/// <summary>The rows of one page and how many rows matched the filter before paging (what a pager needs).</summary>
public sealed record ODataPage<T>(int Count, IReadOnlyList<T> Value);

/// <summary>
/// Runs the OData query of a request against a read model. Only what the admin lists need is allowed (filter, orderby, top,
/// skip, count); everything else, <c>$expand</c> and <c>$apply</c> included, is rejected, and a page can never be larger than
/// <see cref="MaxTop"/>. A request that omits <c>$top</c> gets <see cref="DefaultTop"/> rows, so a list is never unbounded.
/// </summary>
public static class ODataQueryExecutor
{
    public const int MaxTop = 100;
    public const int DefaultTop = 20;

    private static readonly ODataValidationSettings Validation = new()
    {
        AllowedQueryOptions = AllowedQueryOptions.Filter | AllowedQueryOptions.OrderBy | AllowedQueryOptions.Top
            | AllowedQueryOptions.Skip | AllowedQueryOptions.Count,
        MaxTop = MaxTop,
        MaxAnyAllExpressionDepth = 1,
        MaxNodeCount = 50,
    };

    /// <summary>Throws <see cref="ODataException"/> for a query that is malformed or not allowed; the caller answers 400.</summary>
    public static async Task<ODataPage<T>> ExecuteAsync<T>(ODataQueryOptions<T> options, IReadModel<T> readModel, CancellationToken cancellationToken)
    {
        options.Validate(Validation);

        // Every time column is UTC: a date in a $filter is compared as UTC, not in the time zone of the server (the default).
        var settings = new ODataQuerySettings { PageSize = null, EnsureStableOrdering = true, TimeZone = TimeZoneInfo.Utc };
        var source = readModel.Query();
        var filtered = options.Filter is null ? source : (IQueryable<T>)options.Filter.ApplyTo(source, settings);
        var count = await readModel.CountAsync(filtered, cancellationToken);

        // The filter is already applied (and counted). What is left is order, skip and top; EnsureStableOrdering adds the key
        // as the last sort so that pages never overlap or lose rows.
        var paged = (IQueryable<T>)options.ApplyTo(filtered, settings, AllowedQueryOptions.Filter | AllowedQueryOptions.Count);
        var page = options.Top is null ? paged.Take(DefaultTop) : paged;

        return new ODataPage<T>(count, await readModel.ToListAsync(page, cancellationToken));
    }
}
