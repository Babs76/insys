/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Infrastructure
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using System.Linq.Expressions;
using Insys.BuildingBlocks.Application.Context;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Filters a list or search to the records in the caller's scope (F103, addenda/access-scopes.md §4), in the query
/// itself so that paging stays correct: a record is kept when some grant covers both its product and its node.
/// </summary>
public static class ScopeFilter
{
    public static IQueryable<T> InScope<T>(this IQueryable<T> query, AccessScope scope, Expression<Func<T, string?>> product, Expression<Func<T, string?>> orgNode)
    {
        if (scope.IsUnrestricted)
        {
            return query;
        }

        if (scope.Grants.Count == 0)
        {
            return query.Where(_ => false);
        }

        var record = Expression.Parameter(typeof(T), "record");
        var productValue = new Replace(product.Parameters[0], record).Visit(product.Body);
        var nodeValue = new Replace(orgNode.Parameters[0], record).Visit(orgNode.Body);
        Expression? any = null;
        foreach (var grant in scope.Grants)
        {
            Expression covered = Expression.Constant(true);
            if (grant.Products is { } products)
            {
                covered = Expression.AndAlso(covered, Contains(products, productValue));
            }

            if (grant.OrgNodes is { } nodes)
            {
                covered = Expression.AndAlso(covered, Contains(nodes, nodeValue));
            }

            any = any is null ? covered : Expression.OrElse(any, covered);
        }

        return query.Where(Expression.Lambda<Func<T, bool>>(any!, record));
    }

    private sealed class Replace(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    /// <summary>A record with no value (a constant null, as for Health's product, OQ-080) is outside any list of values.</summary>
    private static Expression Contains(IReadOnlySet<string> values, Expression value) =>
        value is ConstantExpression { Value: null }
            ? Expression.Constant(false)
            : Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)], Expression.Constant(values.ToArray()), value);
}
