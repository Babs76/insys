/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Tests
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

namespace Insys.BuildingBlocks.Tests;

/// <summary>AIA-086, OQ-178: a SETUP change set applies each table after the tables of the set it refers to.</summary>
public sealed class SetupOrderTests
{
    [Fact]
    public void ATableRefersToOneAppliedLaterByTheFallback_ComesAfterIt_AIA086()
    {
        // The three the base and motor packs hold, from the catalogue's own references.
        string[] tables = ["workflow.cfg_case_types", "workflow.cfg_workflow_definitions", "communication.cfg_notification_types", "communication.cfg_notification_templates",
            "claims.ref_reserve_types", "finance.cfg_posting_rules", "finance.mst_chart_of_accounts"];
        var order = SetupOrder.Sort(tables).ToList();
        order.IndexOf("claims.ref_reserve_types").ShouldBeGreaterThan(order.IndexOf("finance.cfg_posting_rules"), "a reserve type names its posting rule");
        order.IndexOf("finance.cfg_posting_rules").ShouldBeGreaterThan(order.IndexOf("finance.mst_chart_of_accounts"), "a rule names its accounts");
        order.IndexOf("communication.cfg_notification_templates").ShouldBeGreaterThan(order.IndexOf("communication.cfg_notification_types"));
        order.IndexOf("workflow.cfg_case_types").ShouldBeGreaterThan(order.IndexOf("workflow.cfg_workflow_definitions"));
        order.IndexOf("workflow.cfg_case_types").ShouldBeGreaterThan(order.IndexOf("communication.cfg_notification_types"));
        order.Count.ShouldBe(tables.Length);
    }

    [Fact]
    public void TablesNothingOrders_GoReferenceMasterThenConfiguration_ThenByName_OQ178()
    {
        SetupOrder.Sort(["iam.cfg_b", "x.cfg_a", "z.mst_m", "y.ref_r"], new Dictionary<string, string[]>())
            .ShouldBe(["y.ref_r", "z.mst_m", "iam.cfg_b", "x.cfg_a"]);
    }

    [Fact]
    public void AReferenceOutsideTheSet_OrAToTheTableItself_OrdersNothing_AndACycleFallsBack_OQ178()
    {
        var refs = new Dictionary<string, string[]>
        {
            ["a.ref_x"] = ["a.cfg_y", "a.ref_x", "elsewhere.cfg_z"],
            ["a.cfg_y"] = ["a.ref_x"],
            ["b.cfg_w"] = ["a.cfg_y"],
        };
        SetupOrder.Sort(["b.cfg_w", "a.cfg_y", "a.ref_x"], refs).ShouldBe(["a.ref_x", "a.cfg_y", "b.cfg_w"],
            "a.ref_x and a.cfg_y refer to each other, so the fallback puts reference data first; b.cfg_w follows what it refers to");
    }
}
