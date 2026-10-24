/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Api.StaticData;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.StaticData.Contracts;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Party.Contracts;
using Insys.Workflow.Api.StaticData;
using Insys.Workflow.Application;
using Insys.Workflow.Contracts;
using Insys.Workflow.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Workflow.Api;

public sealed record RejectApprovalRequest(string Reason);

public sealed record StartProcessRequest(string ProcessCode, string BusinessRefType, Guid BusinessRefId);

public sealed record AssignTaskRequest(Guid? AssigneeId);

/// <param name="ToState">The state the process moves to; it must be a defined transition from the current state.</param>
/// <param name="Variables">Values for the transition guard and the owning module's validation.</param>
public sealed record CompleteTaskRequest(string ToState, JsonElement? Variables);

public sealed record CaseNoteRequest(string Note);

public sealed record RejectChangeRequest(string Reason);

/// <summary>The Workflow module: approvals with delegated authority (WFL-004), process instances, tasks, SLA timers and escalation.</summary>
/// <summary>The static data change request a decision was made on.</summary>
public sealed record StaticDataDecision(Guid ChangeRequestId);

public sealed class WorkflowModule : IModule
{
    public string Name => "Workflow";

    /// <summary>A platform module: open to the operator tenant (ADR-024).</summary>
    public bool HoldsBusinessData => false;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddWorkflowInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        WorkflowStaticDataEndpoints.MapStaticData(api);
        var governance = api.MapGroup("").WithMetadata(new StaticDataEndpointMetadata());
        MapStaticDataDecisions(governance.MapGroup("/{module}/static/{entity}").WithTags("StaticData governance"), global: false);
        MapStaticDataDecisions(governance.MapGroup("/{module}/static/{entity}/global").WithTags("StaticData governance"), global: true);
        var workflow = api.MapGroup("/workflow").WithTags(Name);

        var approvals = workflow.MapGroup("/approvals");
        approvals.MapGet("/", async (string? status, string? businessRefType, Guid? businessRefId, string? cursor, int? limit, IDispatcher dispatcher,
                CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListApprovals(status, businessRefType, businessRefId, afterId, pageSize), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<ApprovalView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<ApprovalView>>(StatusCodes.Status200OK)
            .WithName("WorkflowListApprovals")
            .WithSummary("List approvals")
            .RequirePermission(WorkflowPermissions.ApprovalRead);

        approvals.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetApproval(id), ct)).ToHttp(approval =>
                {
                    Concurrency.SetETag(http, approval.RowVersion);
                    return Results.Ok(approval);
                }))
            .Produces<ApprovalView>(StatusCodes.Status200OK)
            .WithName("WorkflowGetApproval")
            .WithSummary("Get an approval")
            .RequirePermission(WorkflowPermissions.ApprovalRead);

        approvals.MapPost("/{id:guid}/approve", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new Approve(id, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("WorkflowApprove")
            .AlsoChecks(PartyPermissions.BankAccountVerify, "the approval is a bank account verification (businessRefType BANK_ACCOUNT; requiredPermission names it)")
            .AlsoChecks(PartyPermissions.PartyUpdate, "the approval is a portal registration match resolution (businessRefType PARTY_MATCH; requiredPermission names it)")
            .WithSummary("Approve (not by the maker, within the checker's authority)")
            .WithDescription("The checker needs a role with authority for the approval type and, where there is an amount, a limit at least that amount (WFL-004).")
            .WithEventualEffects("the decision reaches the record that asked for it (a static data version, product version, role assignment, bank "
                + "account, payment or claim instruction, first tenant administrator ...) and its own follow-ups, such as a product's publication; read that "
                + "record again to see it.")
            .RequirePermission(WorkflowPermissions.ApprovalApprove);

        approvals.MapPost("/{id:guid}/reject", (Guid id, RejectApprovalRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new Reject(id, request.Reason, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("WorkflowReject")
            .AlsoChecks(PartyPermissions.BankAccountVerify, "the approval is a bank account verification (businessRefType BANK_ACCOUNT; requiredPermission names it)")
            .AlsoChecks(PartyPermissions.PartyUpdate, "the approval is a portal registration match resolution (businessRefType PARTY_MATCH; requiredPermission names it)")
            .WithSummary("Reject, with a reason")
            .WithEventualEffects("the rejection reaches the record that asked for it, which returns to its previous state; read that record again.")
            .RequirePermission(WorkflowPermissions.ApprovalApprove);

        var processes = workflow.MapGroup("/processes");
        processes.MapPost("/", async (StartProcessRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new StartProcess(request.ProcessCode, request.BusinessRefType, request.BusinessRefId), ct)).ToHttp(id =>
                {
                    Concurrency.SetETag(http, 1);
                    return Results.Created($"/api/v1/workflow/processes/{id}", new { id });
                }))
            .Produces<CreatedId>(StatusCodes.Status201Created)
            .WithName("WorkflowStartProcess")
            .WithSummary("Start a process from the workflow definition in force")
            .RequirePermission(WorkflowPermissions.ProcessCreate)
            .RequireIdempotencyKey();

        processes.MapGet("/", async (string? businessRefType, Guid? businessRefId, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListProcesses(businessRefType, businessRefId, afterId, pageSize), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<ProcessView>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<ProcessView>>(StatusCodes.Status200OK)
            .WithName("WorkflowListProcesses")
            .WithSummary("List process instances")
            .RequirePermission(WorkflowPermissions.ProcessRead);

        processes.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetProcess(id), ct)).ToHttp(process =>
                {
                    Concurrency.SetETag(http, process.RowVersion);
                    return Results.Ok(process);
                }))
            .Produces<ProcessView>(StatusCodes.Status200OK)
            .WithName("WorkflowGetProcess")
            .WithSummary("Get a process instance with its tasks and case notes")
            .RequirePermission(WorkflowPermissions.ProcessRead);

        foreach (var action in Enum.GetValues<ProcessAction>())
        {
            var verb = action.ToString().ToLowerInvariant();
            processes.MapPost($"/{{id:guid}}/{verb}", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                    Versioned(http, version => dispatcher.Send(new ChangeProcess(id, action, version), ct)))
                .Produces(StatusCodes.Status204NoContent)
                .RequireIfMatch()
                .WithName($"Workflow{action}Process")
                .WithSummary($"{action} a process")
                .RequirePermission(WorkflowPermissions.ProcessUpdate);
        }

        processes.MapPost("/{id:guid}/notes", (Guid id, CaseNoteRequest request, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new AddCaseNote(id, request.Note, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("WorkflowAddCaseNote")
            .WithSummary("Add a case note")
            .RequirePermission(WorkflowPermissions.ProcessUpdate);

        processes.MapPost("/{id:guid}/tasks/{taskId:guid}/assign", (Guid id, Guid taskId, AssignTaskRequest request, IDispatcher dispatcher, HttpContext http,
                CancellationToken ct) => Versioned(http, version => dispatcher.Send(new AssignTask(id, taskId, request.AssigneeId, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("WorkflowAssignTask")
            .WithSummary("Assign or reassign a task; without an assignee the caller takes it")
            .RequirePermission(WorkflowPermissions.ProcessUpdate);

        processes.MapPost("/{id:guid}/tasks/{taskId:guid}/complete", (Guid id, Guid taskId, CompleteTaskRequest request, IDispatcher dispatcher, HttpContext http,
                CancellationToken ct) => Versioned(http, version => dispatcher.Send(new CompleteTask(id, taskId, request.ToState, request.Variables, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("WorkflowCompleteTask")
            .WithSummary("Complete a task and move the process along a defined transition")
            .WithDescription("The transition guard is evaluated on the variables, then the owning module's validation (by DomainValidation code) runs; either can refuse.")
            .RequirePermission(WorkflowPermissions.ProcessUpdate);

        processes.MapPost("/{id:guid}/tasks/{taskId:guid}/escalate", async (Guid id, Guid taskId, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new EscalateTask(id, taskId), ct)).ToHttp(version =>
                {
                    Concurrency.SetETag(http, version);
                    return Results.NoContent();
                }))
            .Produces(StatusCodes.Status204NoContent)
            .WithName("WorkflowEscalateTask")
            .WithSummary("Escalate a task to the next escalation level")
            .RequirePermission(WorkflowPermissions.ProcessUpdate);

        workflow.MapGet("/tasks", async (string? queueCode, Guid? assigneeId, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListOpenTasks(queueCode, assigneeId, Cursor.ClampLimit(limit)), ct)).ToHttp(items => Results.Ok(items)))
            .Produces<IReadOnlyList<OpenTaskView>>(StatusCodes.Status200OK)
            .WithName("WorkflowListOpenTasks")
            .WithSummary("List open tasks by queue or assignee, earliest due first")
            .RequirePermission(WorkflowPermissions.ProcessRead);
    }

    /// <summary>
    /// Approve and reject for every static data table, on the same paths as each table's lifecycle actions. The change
    /// request is found through Static Data's public query and decided as a Workflow approval (ADR-009).
    /// </summary>
    private static void MapStaticDataDecisions(RouteGroupBuilder group, bool global)
    {
        var suffix = global ? "Global" : "";
        var approve = group.MapPost("/{code}/versions/{versionNumber:int}/approve", (string module, string entity, string code, int versionNumber,
                IEnumerable<ModuleStaticDataTables> modules, IStaticDataChangeLookup lookup, ITenantContext tenant, IDispatcher dispatcher, CancellationToken ct) =>
                DecideStaticDataChange(modules, lookup, tenant, dispatcher, module, entity, code, versionNumber, global, approve: true, reason: null, ct))
            .Produces<StaticDataDecision>(StatusCodes.Status200OK)
            .WithName($"ApproveStaticDataChange{suffix}")
            .WithSummary("Approve the pending change of a static data version")
            .WithDescription("The checker must not be the maker for Tier 1 and Tier 2 data (SDM-003). Workflow records the approval; the owning module then marks the version approved.");

        var reject = group.MapPost("/{code}/versions/{versionNumber:int}/reject", (string module, string entity, string code, int versionNumber,
                RejectChangeRequest request, IEnumerable<ModuleStaticDataTables> modules, IStaticDataChangeLookup lookup, ITenantContext tenant,
                IDispatcher dispatcher, CancellationToken ct) =>
                DecideStaticDataChange(modules, lookup, tenant, dispatcher, module, entity, code, versionNumber, global, approve: false, request.Reason, ct))
            .Produces<StaticDataDecision>(StatusCodes.Status200OK)
            .WithName($"RejectStaticDataChange{suffix}")
            .WithSummary("Reject the pending change of a static data version; it returns to draft");

        if (global)
        {
            approve.RequirePlatformPermission(StaticDataPermissions.Approve);
            reject.RequirePlatformPermission(StaticDataPermissions.Approve);
        }
        else
        {
            approve.RequirePermission(StaticDataPermissions.Approve);
            reject.RequirePermission(StaticDataPermissions.Approve);
        }
    }

    private static async Task<IResult> DecideStaticDataChange(IEnumerable<ModuleStaticDataTables> modules, IStaticDataChangeLookup lookup, ITenantContext tenant,
        IDispatcher dispatcher, string module, string entity, string code, int version, bool global, bool approve, string? reason, CancellationToken ct)
    {
        var descriptor = modules.Where(m => m.ModuleRoute == module).SelectMany(m => m.Descriptors).FirstOrDefault(d => d.EntityRoute == entity);
        if (descriptor is null)
        {
            return Problems.For(BuildingBlocks.Domain.ErrorCodes.NotFound, "There is no such static data entity.", StatusCodes.Status404NotFound);
        }

        if (await lookup.PendingChange(tenant.RequireTenantId(), descriptor.EntityCode, code, version, global, ct) is not { } changeRequestId)
        {
            return Problems.For(BuildingBlocks.Domain.ErrorCodes.NotFound,
                "No change request is pending for this version. Submit it first; a just-submitted change appears within seconds.", StatusCodes.Status404NotFound);
        }

        return (await dispatcher.Send(new DecideByReference(StaticDataApprovals.BusinessRefType, changeRequestId, approve, reason), ct))
            .ToHttp(_ => Results.Ok(new StaticDataDecision(changeRequestId)));
    }

    private static async Task<IResult> Versioned(HttpContext http, Func<long, Task<BuildingBlocks.Domain.Result<long>>> send)
    {
        var expected = Concurrency.ExpectedVersion(http.Request);
        if (expected.IsFailure)
        {
            return Problems.For(expected.Error);
        }

        return (await send(expected.Value)).ToHttp(version =>
        {
            Concurrency.SetETag(http, version);
            return Results.NoContent();
        });
    }
}
