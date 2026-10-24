using System.Linq.Expressions;
using Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadModels;

/// <summary>Runs a composed query on EF Core. A read model only has to say what <see cref="Query"/> projects.</summary>
public abstract class EfReadModel<T> : IReadModel<T>
{
    public abstract IQueryable<T> Query();

    public Task<int> CountAsync(IQueryable<T> query, CancellationToken cancellationToken) =>
        Prepare(query).CountAsync(cancellationToken);

    public async Task<IReadOnlyList<T>> ToListAsync(IQueryable<T> query, CancellationToken cancellationToken) =>
        await Prepare(query).ToListAsync(cancellationToken);

    /// <summary>
    /// Every time column is <c>timestamp</c> (no zone) holding UTC, and Npgsql refuses <c>Kind=Utc</c> for it. A date the caller
    /// wrote into the query (an OData filter) is UTC by definition, so it is sent as <c>Unspecified</c>, like <c>ToTimestamp</c> in
    /// <c>CandleRepository</c> does.
    /// </summary>
    private static IQueryable<T> Prepare(IQueryable<T> query) =>
        query.Provider.CreateQuery<T>(new UnspecifiedDateTimeVisitor().Visit(query.Expression));

    /// <summary>
    /// A date from the caller is a constant, or a closure field holding one, possibly converted from a DateTimeOffset: whatever
    /// part of the query yields a DateTime without looking at the row is evaluated, and replaced when it is UTC.
    /// </summary>
    private sealed class UnspecifiedDateTimeVisitor : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
        {
            if (node is null or ParameterExpression or LambdaExpression
                || node.Type != typeof(DateTime) && node.Type != typeof(DateTime?)
                || !DependsOnNothing.Check(node))
            {
                return base.Visit(node);
            }

            var value = Expression.Lambda(node).Compile().DynamicInvoke();
            return value is DateTime { Kind: DateTimeKind.Utc } utc
                ? Expression.Constant(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), node.Type)
                : node;
        }
    }

    /// <summary>True when an expression has no parameter (no column of the row) in it, so it can be evaluated on its own.</summary>
    private sealed class DependsOnNothing : ExpressionVisitor
    {
        private bool _usesParameter;

        public static bool Check(Expression expression)
        {
            var finder = new DependsOnNothing();
            finder.Visit(expression);
            return !finder._usesParameter;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _usesParameter = true;
            return node;
        }
    }
}
