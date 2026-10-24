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
using Insys.BuildingBlocks.Application.Context;
using Npgsql;
using NpgsqlTypes;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The caller's scope (F103) as a SQL predicate, for queries written in SQL rather than LINQ (as <see cref="ScopeFilter"/>
/// is): a row is kept when some grant covers both its product and its node. Values go in as parameters.
/// </summary>
public static class ScopeSql
{
    public static string Predicate(AccessScope scope, string productColumn, string nodeColumn, NpgsqlCommand command)
    {
        if (scope.IsUnrestricted)
        {
            return "true";
        }

        if (scope.Grants.Count == 0)
        {
            return "false";
        }

        var any = new List<string>();
        for (var i = 0; i < scope.Grants.Count; i++)
        {
            var grant = scope.Grants[i];
            var all = new List<string>();
            if (grant.Products is { } products)
            {
                command.Parameters.Add(new NpgsqlParameter($"scope_p{i}", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = products.ToArray() });
                all.Add($"{productColumn} = ANY(@scope_p{i})");
            }

            if (grant.OrgNodes is { } nodes)
            {
                command.Parameters.Add(new NpgsqlParameter($"scope_n{i}", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = nodes.ToArray() });
                all.Add($"{nodeColumn} = ANY(@scope_n{i})");
            }

            any.Add(all.Count == 0 ? "true" : "(" + string.Join(" AND ", all) + ")");
        }

        return "(" + string.Join(" OR ", any) + ")";
    }
}
