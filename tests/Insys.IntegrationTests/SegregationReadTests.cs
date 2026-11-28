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
using System.Text.Json;
using Insys.Seed;

namespace Insys.IntegrationTests;

/// <summary>
/// The Preview's read of segregation-of-duties conflicts (tools/Insys.Seed --segregation-conflicts, Ade, 28 November 2026): its report names each
/// conflict's rule and functions only, never the user, and says "none" when the list is empty. No database.
/// </summary>
public sealed class SegregationReadTests
{
    [Fact]
    public void AnEmptyList_ReportsNone()
    {
        var output = new StringWriter();
        SegregationRead.Report(JsonDocument.Parse("[]").RootElement, output).ShouldBe(SegregationRead.Read);
        output.ToString().Trim().ShouldBe("segregation-conflicts: OFTZA: none");
    }

    [Fact]
    public void Conflicts_AreReportedByRuleAndFunctions_NeverByUser_SRS002()
    {
        var user = Guid.Parse("0199f000-0000-7000-8000-0000000000c1");
        using var conflicts = JsonDocument.Parse($$"""
            [{"userId":"{{user}}","rule":"SOD_PAYOUT_BATCH","baseline":true,"functionA":"PAYMENTS_BUILD_BATCH","functionB":"PAYMENTS_APPROVE_BATCH","refusedFunction":"PAYMENTS_APPROVE_BATCH"},
             {"userId":"{{user}}","rule":"SOD_CLAIM_PAYMENT","baseline":true,"functionA":"CLAIMS_ASSESS","functionB":"CLAIMS_PAY","refusedFunction":"CLAIMS_PAY"}]
            """);
        var output = new StringWriter();

        SegregationRead.Report(conflicts.RootElement, output).ShouldBe(SegregationRead.Read);

        output.ToString().Trim().ShouldBe("segregation-conflicts: OFTZA: 2: SOD_CLAIM_PAYMENT (CLAIMS_ASSESS and CLAIMS_PAY); SOD_PAYOUT_BATCH (PAYMENTS_BUILD_BATCH and PAYMENTS_APPROVE_BATCH)");
        output.ToString().ShouldNotContain(user.ToString());
    }
}
