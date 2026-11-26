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
using Insys.Workflow.Application;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-170 (GAP-179): the record types a process or task can carry are the approval reference types and Workflow's process-only types, each opening a
/// screen (BR-005), and the contract lists exactly those for a task's and a process's record type. Every process start goes through the guard
/// (ProcessRecordTypes.Start, ProcessStartRuleTests), so a type missing from the list fails where it is opened, not in the front end.
/// </summary>
public sealed class TaskRecordTypesTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly string[] TaskViews = ["WorkflowMyTaskView", "WorkflowProcessView", "WorkflowStartProcessRequest"];

    [Fact]
    public async Task EveryTaskRecordType_OpensAScreenInTheCatalogue_BR005_BR170()
    {
        await fixture.Host();
        var screens = FunctionRegistry.All().Where(f => f.Function.Kind == FunctionKind.Screen).Select(f => f.Function.Code).ToHashSet(StringComparer.Ordinal);
        TaskRecordTypes.All.Where(t => !screens.Contains(TaskRecordTypes.ScreenFor(t) ?? "")).ShouldBeEmpty();
        TaskRecordTypes.All.ShouldBe(ProcessRecordTypes.All, "the contract's list is Workflow's one list and the approval reference types");
        TaskRecordTypes.ProcessOnly.ShouldBe([.. ProcessRecordTypes.ProcessOnly.Keys.Order(StringComparer.Ordinal)]);
        ProcessRecordTypes.ProcessOnly.Keys.Intersect(ApprovalKinds.BusinessRefTypes, StringComparer.Ordinal)
            .ShouldBeEmpty("a type is declared once: as an approval reference type or as a process-only type, so its screen has one source");
        ApprovalNextSteps.All.Select(s => s.BusinessRefType).Where(t => !TaskRecordTypes.All.Contains(t)).ShouldBeEmpty("a step after an approval is about a declared type");
        foreach (var type in TaskRecordTypes.All)
        {
            WorkServers.ScreenFor(type).ShouldBe(TaskRecordTypes.ScreenFor(type), type);
        }
    }

    [Fact]
    public async Task TheContract_ListsEveryTaskRecordType_AndApprovalsKeepTheirOwn_BR170()
    {
        // The committed document: OpenApiContractTests checks it is the one the host serves.
        await fixture.Host();
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(RepositoryPaths.Root, "docs", "api", "openapi-core.json")));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var view in TaskViews)
        {
            schemas.GetProperty(view).GetProperty("properties").GetProperty("businessRefType").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
                .ShouldBe(TaskRecordTypes.All, view);
        }

        schemas.GetProperty("WorkflowApprovalView").GetProperty("properties").GetProperty("businessRefType").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()).ShouldBe(ApprovalKinds.BusinessRefTypes, "an approval is only about an approval reference type");
    }
}
