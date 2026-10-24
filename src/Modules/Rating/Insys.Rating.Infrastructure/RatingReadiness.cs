/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Rating
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
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Infrastructure.Readiness;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.ProductFactory.Contracts;
using Insys.Rating.Application;
using Insys.Rating.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Rating.Infrastructure;

/// <summary>
/// Onboarding readiness RATING (PRODUCT_FACTORY): rating tables in force, and for every sellable product version a rating
/// test case library that passes (PRD-005), run as the tenant through the same rater a quote uses.
/// </summary>
internal sealed class RatingReadiness([FromKeyedServices(RatingDbContext.SchemaName)] NpgsqlDataSource dataSource, IPublishedProducts products,
    IServiceScopeFactory scopes) : IReadinessCheck
{
    public IReadOnlyCollection<string> Steps { get; } = ["PRODUCT_FACTORY"];

    public async Task<IReadOnlyList<ReadinessResult>> Check(ReadinessContext context, CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        var tables = await ReadinessSql.InTenant(dataSource, context.TenantId, (connection, transaction) => ReadinessSql.Count(connection, transaction,
            $"SELECT count(*) FROM rating.cfg_rating_tables WHERE tenant_id = @t AND {ReadinessSql.InForce}", cancellationToken,
            ("t", context.TenantId), ("inForce", StaticLifecycle.Configuration.InForce), ("day", context.Day)), cancellationToken);
        if (tables == 0)
        {
            missing.Add("rating.cfg_rating_tables");
        }

        foreach (var (code, version) in await products.Sellable(context.TenantId, context.Day, cancellationToken))
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(context.TenantId, "onboarding-readiness");
            var run = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new RunRatingTestCases(code, version, context.Day), cancellationToken);
            if (run.IsFailure)
            {
                missing.Add($"{code} v{version}: {run.Error.Code}");
            }
            else if (run.Value.Count == 0)
            {
                missing.Add($"{code} v{version}: rating.cfg_rating_test_case_library");
            }
            else
            {
                missing.AddRange(run.Value.Where(r => !r.Passed).Select(r => $"{code} v{version}: test case {r.CaseId} fails"));
            }
        }

        return [ReadinessResult.Of("PRODUCT_FACTORY", "RATING", "Rating tables in force, and every published product's rating test cases pass (PRD-005)", missing)];
    }
}
