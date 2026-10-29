/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// One time source (V0093, Ade, 29 October 2026): no function, view or row-level policy in force decides by the database's
/// clock. Due times, cut-overs and months come from the application's clock, passed in as a parameter. The few that read the
/// database's clock only to stamp a row they write are listed, each with why; anything new fails here until it is moved to a
/// parameter or reviewed into the list.
/// </summary>
public sealed class RecordTimeSourceTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    /// <summary>Reviewed: they stamp the row they write; nothing is decided by the time.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["function general.record_status_history()"] = "V0064 trigger: stamps the history row it writes; the change's time is the application's updated_at whenever it set one",
        ["function health.record_status_history()"] = "V0064 trigger: as General's",
        ["function life.record_status_history()"] = "V0064 trigger: as General's",
        ["function iam.adopt_function_catalogue()"] = "Release-time conversion run by the migrator (ADR-026 §7), with no application running: the release's date is the database's",
    };

    private const string Clock = @"(now\s*\(\s*\)|current_timestamp|current_date|current_time|localtimestamp|localtime|clock_timestamp\s*\(|statement_timestamp\s*\(|transaction_timestamp\s*\()";

    [Fact]
    public async Task NoFunctionViewOrPolicy_DecidesByTheDatabasesClock_V0093()
    {
        var found = (await fixture.Database.ScalarAsOwner<string>($"""
            SELECT coalesce(string_agg(item, E'\n' ORDER BY item), '') FROM (
              SELECT 'function ' || n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')' AS item
              FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace JOIN pg_language l ON l.oid = p.prolang
              WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'public', 'test_mod') AND n.nspname NOT LIKE 'pg\_%' AND l.lanname IN ('sql', 'plpgsql')
                AND p.prosrc ~* '{Clock}'
              UNION ALL
              SELECT 'view ' || schemaname || '.' || viewname FROM pg_views
              WHERE schemaname NOT IN ('pg_catalog', 'information_schema') AND definition ~* '{Clock}'
              UNION ALL
              SELECT 'policy ' || schemaname || '.' || tablename || '.' || policyname FROM pg_policies
              WHERE coalesce(qual, '') ~* '{Clock}' OR coalesce(with_check, '') ~* '{Clock}'
            ) x
            """))!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        found.Where(f => !Allowed.ContainsKey(f)).ShouldBeEmpty("decide by the application's clock, passed in as a parameter");
        Allowed.Keys.Where(a => !found.Contains(a)).ShouldBeEmpty("an allowed entry no longer exists: remove it");
    }
}
