/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : StaticData.Tests
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
using Insys.StaticData.Application.Changes;

namespace Insys.StaticData.Tests;

/// <summary>BR-015: a change request leads to its version: the version number and the table's API route.</summary>
public sealed class ChangeRequestLinksTests
{
    private static JsonElement Record(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void TenantRecord_GivesTheTablesRoute_AndTheVersionFromTheReference_BR015()
    {
        var record = Record("""
            {"schema":"billing","table":"ref_billing_frequencies","entityCode":"ref_billing_frequencies","moduleRoute":"billing",
             "entityRoute":"billing-frequencies","recordId":"0199f000-0000-7000-8000-000000000009","code":"MONTHLY","version":3,"global":false}
            """);

        ChangeRequestLinks.VersionNumber("MONTHLY@3", record).ShouldBe(3);
        ChangeRequestLinks.EntityRoute("MONTHLY@3", record).ShouldBe("billing/static/billing-frequencies");
    }

    [Fact]
    public void GlobalRecord_GivesTheGlobalRoute_BR015()
    {
        var record = Record("""{"moduleRoute":"billing","entityRoute":"billing-frequencies","code":"MONTHLY","version":2,"global":true}""");

        ChangeRequestLinks.EntityRoute("G:MONTHLY@2", record).ShouldBe("billing/static/billing-frequencies/global");
    }

    [Fact]
    public void ReferenceWithoutRoutes_GivesNoRoute_AndTheVersionFromTheRecordKey_BR015()
    {
        var record = Record("{}");

        ChangeRequestLinks.VersionNumber("G:ZAR@7", record).ShouldBe(7);
        ChangeRequestLinks.EntityRoute("ZAR@1", record).ShouldBeNull();
    }
}
