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
    private static readonly string[] TaskViews = ["WorkflowMyTaskView", "WorkflowProcessView"];

    [Fact]
    public async Task EveryTaskRecordType_OpensAScreenInTheCatalogue_BR005_BR170()
    {
        await fixture.Host();
        var screens = FunctionRegistry.All().Where(f => f.Function.Kind == FunctionKind.Screen).Select(f => f.Function.Code).ToHashSet(StringComparer.Ordinal);
        TaskRecordTypes.Startable.Where(t => !screens.Contains(TaskRecordTypes.ScreenFor(t) ?? "")).ShouldBeEmpty();
        TaskRecordTypes.Startable.ShouldBe(ProcessRecordTypes.All, "the contract's list is Workflow's one list and the approval reference types");
        TaskRecordTypes.ProcessOnly.ShouldBe([.. ProcessRecordTypes.ProcessOnly.Keys.Order(StringComparer.Ordinal)]);
        ProcessRecordTypes.ProcessOnly.Keys.Intersect(ApprovalKinds.BusinessRefTypes, StringComparer.Ordinal)
            .ShouldBeEmpty("a type is declared once: as an approval reference type or as a process-only type, so its screen has one source");
        ApprovalNextSteps.All.Select(s => s.BusinessRefType).Where(t => !TaskRecordTypes.Startable.Contains(t)).ShouldBeEmpty("a step after an approval is about a declared type");
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

        schemas.GetProperty("WorkflowStartProcessRequest").GetProperty("properties").GetProperty("businessRefType").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()).ShouldBe(TaskRecordTypes.Startable, "UNDECLARED is shown, never started about");
        TaskRecordTypes.All.ShouldBe([.. ProcessRecordTypes.All.Append(TaskRecordTypes.Undeclared).Order(StringComparer.Ordinal)],
            "the views' enum is the declared set and UNDECLARED");
        foreach (var view in TaskViews)
        {
            var stored = schemas.GetProperty(view).GetProperty("properties").GetProperty("storedBusinessRefType");
            stored.ToString().ShouldContain("null", Case.Sensitive, view);
            schemas.GetProperty(view).GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("storedBusinessRefType", view);
        }

        schemas.GetProperty("WorkflowApprovalView").GetProperty("properties").GetProperty("businessRefType").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()).ShouldBe(ApprovalKinds.BusinessRefTypes, "an approval is only about an approval reference type");
    }

    /// <summary>Ade, 26 November 2026: UNDECLARED is what a view shows; it opens no screen, can't be declared, and no process starts about it.</summary>
    [Fact]
    public async Task Undeclared_OpensNoScreen_AndNoProcessStartsAboutIt_BR170()
    {
        await fixture.Host();
        ApprovalKinds.BusinessRefTypes.ShouldNotContain(TaskRecordTypes.Undeclared);
        ProcessRecordTypes.ProcessOnly.Keys.ShouldNotContain(TaskRecordTypes.Undeclared);
        TaskRecordTypes.ScreenFor(TaskRecordTypes.Undeclared).ShouldBeNull();
        WorkServers.ScreenFor(TaskRecordTypes.Undeclared).ShouldBeNull();
        ProcessRecordTypes.IsDeclared(TaskRecordTypes.Undeclared).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => TaskRecordTypes.Declare(TaskRecordTypes.Undeclared, "PARTIES"));

        var validation = new StartProcessValidator().Validate(new StartProcess("CLAIM", TaskRecordTypes.Undeclared, Guid.CreateVersion7()));
        validation.Errors.Select(e => e.PropertyName).ShouldContain(nameof(StartProcess.BusinessRefType));
        Should.Throw<InvalidOperationException>(() => ProcessRecordTypes.Start(Guid.CreateVersion7(), Insys.Workflow.Domain.Processes.BuiltInProcesses.ParkedDelivery,
            TaskRecordTypes.Undeclared, Guid.CreateVersion7(), _ => throw new InvalidOperationException("not routed"), DateTimeOffset.UtcNow));
    }

    /// <summary>Ade, 26 November 2026: a stored type is shown as itself when declared, as its declared equivalent, or as UNDECLARED, keeping the stored value.</summary>
    [Fact]
    public async Task AStoredRecordType_IsShownAsItself_ItsEquivalent_OrUndeclared_BR170()
    {
        await fixture.Host();
        var equivalents = new Dictionary<string, string>(StringComparer.Ordinal) { ["OLD_LIFE_CLAIM"] = "LIFE_CLAIM", ["OLD_GONE"] = "NO_SUCH_TYPE" };
        ProcessRecordTypes.Shown("LIFE_CLAIM", equivalents).ShouldBe(("LIFE_CLAIM", (string?)null));
        ProcessRecordTypes.Shown("OLD_LIFE_CLAIM", equivalents).ShouldBe(("LIFE_CLAIM", (string?)"OLD_LIFE_CLAIM"));
        ProcessRecordTypes.Shown("OLD_GONE", equivalents).ShouldBe((TaskRecordTypes.Undeclared, (string?)"OLD_GONE"), "an equivalent that isn't declared opens nothing");
        ProcessRecordTypes.Shown("CLAIM", equivalents).ShouldBe((TaskRecordTypes.Undeclared, (string?)"CLAIM"));
        ProcessRecordTypes.Shown(TaskRecordTypes.Undeclared, equivalents).ShouldBe((TaskRecordTypes.Undeclared, (string?)TaskRecordTypes.Undeclared),
            "a stored UNDECLARED (typed before the narrowing) is undeclared too");

        ProcessRecordTypes.StoredFor("LIFE_CLAIM", equivalents).ShouldBe(["LIFE_CLAIM", "OLD_LIFE_CLAIM"], "a filter by the shown type finds the older value");
        ProcessRecordTypes.StoredFor("OLD_LIFE_CLAIM", equivalents).ShouldBe(["OLD_LIFE_CLAIM"], "a filter by the stored value finds it");
        ProcessRecordTypes.StoredFor("CLAIM", equivalents).ShouldBe(["CLAIM"]);
        ProcessRecordTypes.StoredFor(TaskRecordTypes.Undeclared, equivalents).ShouldBeNull("UNDECLARED matches every stored value not shown otherwise");
    }

    /// <summary>Ade, 26 November 2026: each older type's equivalent is declared, and the older type itself is not (or it would show as itself).</summary>
    [Fact]
    public async Task EveryEquivalent_IsADeclaredType_ForAnUndeclaredOne_BR170()
    {
        await fixture.Host();
        ProcessRecordTypes.Equivalents.Where(e => !ProcessRecordTypes.IsDeclared(e.Value)).ShouldBeEmpty("an equivalent opens its record");
        ProcessRecordTypes.Equivalents.Keys.Where(ProcessRecordTypes.IsDeclared).ShouldBeEmpty("a declared type is shown as itself");
        ProcessRecordTypes.Equivalents.Keys.ShouldNotContain(TaskRecordTypes.Undeclared);
        ProcessRecordTypes.Known.ShouldBe([.. ProcessRecordTypes.All, .. ProcessRecordTypes.Equivalents.Keys], ignoreOrder: true);
    }
}
