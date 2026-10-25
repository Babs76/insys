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
using Insys.BuildingBlocks.Api.Functions;
using Insys.BuildingBlocks.Contracts.Approvals;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-005: the approval and reference types the modules declare are the contract's enums, and each reference type names
/// the screen that opens its record. Workflow refuses an approval of an undeclared kind, so the approval tables tests
/// (every type, through the real request) prove each module uses a declared value.
/// </summary>
public sealed class ApprovalKindsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task EveryReferenceType_OpensAScreenInTheCatalogue_BR005()
    {
        await fixture.Host();
        var screens = FunctionRegistry.All().Where(f => f.Function.Kind == FunctionKind.Screen).Select(f => f.Function.Code).ToHashSet(StringComparer.Ordinal);
        ApprovalKinds.BusinessRefTypes.Count.ShouldBe(18);
        ApprovalKinds.ApprovalTypes.Count.ShouldBe(18);
        ApprovalKinds.BusinessRefTypes.Where(r => !screens.Contains(ApprovalKinds.ScreenFor(r)!)).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheContract_ListsTheDeclaredTypes_AndAnApprovalNamesItsScreen_BR005()
    {
        var factory = await fixture.Host();
        using var document = System.Text.Json.JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/openapi/core.json"));
        var view = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("WorkflowApprovalView").GetProperty("properties");
        view.GetProperty("approvalType").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ShouldBe(ApprovalKinds.ApprovalTypes);
        view.GetProperty("businessRefType").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ShouldBe(ApprovalKinds.BusinessRefTypes);
        view.TryGetProperty("screen", out _).ShouldBeTrue();
    }
}
