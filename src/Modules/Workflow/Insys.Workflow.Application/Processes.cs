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
using FluentValidation;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Workflow.Domain.Approvals;
using Insys.Workflow.Domain.Processes;

namespace Insys.Workflow.Application;

/// <summary>Workflow configuration in force: definitions, routing of task types to queues and SLAs, and escalation rules.</summary>
public interface IWorkflowConfiguration
{
    /// <summary>The definition version in force today, or a specific version (WFL-001).</summary>
    Task<Result<ProcessDefinition>> Definition(string processCode, int? version, DateOnly day, CancellationToken cancellationToken);

    /// <summary>A function giving each task type its queue, SLA and due time.</summary>
    Task<Func<string, TaskRouting>> Routing(DateOnly day, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The escalation rule for an SLA breach after <paramref name="currentLevel"/>, if any.</summary>
    Task<(int Level, string TargetRole)?> NextEscalation(int currentLevel, DateOnly day, CancellationToken cancellationToken);
}

public interface IProcessRepository
{
    Task<ProcessInstance?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> WithDueTimers(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    void Add(ProcessInstance process);
}

public sealed record TaskView(Guid Id, string TaskType, string QueueCode, Guid? AssigneeId, Domain.Processes.TaskStatus Status, string? SlaCode, DateTimeOffset DueAt,
    DateTimeOffset? CompletedAt, string? Outcome);

public sealed record OpenTaskView(Guid ProcessId, TaskView Task);

public sealed record CaseNoteView(Guid AuthorId, string Note, DateTimeOffset CreatedAt);

/// <summary>A transition the process may take from its current state (BR-028): completing its open task with another is refused.</summary>
/// <param name="Conditional">The transition has a guard, evaluated on the variables sent with the completion; it may still be refused.</param>
public sealed record ProcessTransitionView(string ToState, bool Conditional);

/// <param name="AllowedTransitions">
/// From the current state, in the definition version the process started on (BR-028); empty unless the process is running.
/// </param>
public sealed record ProcessView(Guid Id, string ProcessCode, int ProcessVersion, string BusinessRefType, Guid BusinessRefId, ProcessStatus Status, string State,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, IReadOnlyList<TaskView> Tasks, IReadOnlyList<CaseNoteView> Notes, long RowVersion,
    IReadOnlyList<ProcessTransitionView>? AllowedTransitions = null)
{
    /// <summary>The view with its allowed transitions, from the definitions already read (one read per definition version).</summary>
    public static async Task<ProcessView> WithTransitions(ProcessView view, IWorkflowConfiguration configuration, DateOnly day,
        Dictionary<(string, int), ProcessDefinition?> definitions, CancellationToken cancellationToken)
    {
        if (view.Status != ProcessStatus.Running)
        {
            return view with { AllowedTransitions = [] };
        }

        var key = (view.ProcessCode, view.ProcessVersion);
        if (!definitions.TryGetValue(key, out var definition))
        {
            var read = await configuration.Definition(view.ProcessCode, view.ProcessVersion, day, cancellationToken);
            definitions[key] = definition = read.IsSuccess ? read.Value : null;
        }

        return view with
        {
            AllowedTransitions = definition is null
                ? []
                : definition.Transitions.Where(t => t.From == view.State).GroupBy(t => t.To)
                    .Select(g => new ProcessTransitionView(g.Key, g.All(t => t.Guard is not null))).ToList(),
        };
    }
}

/// <summary>A page of open tasks, earliest due first: the cursor is the last task's due time and ID (BR-028).</summary>
public sealed record OpenTaskCursor(DateTimeOffset DueAt, Guid Id);

public interface IProcessQueries
{
    Task<IReadOnlyList<ProcessView>> List(string? businessRefType, Guid? businessRefId, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<ProcessView?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<OpenTaskView>> OpenTasks(string? queueCode, Guid? assigneeId, OpenTaskCursor? after, int limit, CancellationToken cancellationToken);
}

public sealed record StartProcess(string ProcessCode, string BusinessRefType, Guid BusinessRefId) : ICommand<Guid>;

/// <summary>AssignTask (or ReassignTask when the task already has an assignee). No assignee means the caller takes it.</summary>
public sealed record AssignTask(Guid ProcessId, Guid TaskId, Guid? AssigneeId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record CompleteTask(Guid ProcessId, Guid TaskId, string ToState, JsonElement? Variables, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public enum ProcessAction
{
    Suspend,
    Resume,
    Cancel,
}

public sealed record ChangeProcess(Guid ProcessId, ProcessAction Action, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record AddCaseNote(Guid ProcessId, string Note, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>Fires a process's due SLA timers (the timer job).</summary>
public sealed record FireTimers(Guid ProcessId) : ICommand<int>;

/// <summary>EscalateTask, from SlaBreached or by hand.</summary>
public sealed record EscalateTask(Guid ProcessId, Guid TaskId) : ICommand<long>;

public sealed record ListProcesses(string? BusinessRefType, Guid? BusinessRefId, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ProcessView>>;

public sealed record GetProcess(Guid ProcessId) : IQuery<ProcessView>;

public sealed record ListOpenTasks(string? QueueCode, Guid? AssigneeId, int Limit, OpenTaskCursor? After = null) : IQuery<IReadOnlyList<OpenTaskView>>;

public sealed record ProcessesWithDueTimers(DateTimeOffset Now) : IQuery<IReadOnlyList<Guid>>;

public sealed class StartProcessValidator : AbstractValidator<StartProcess>
{
    public StartProcessValidator()
    {
        RuleFor(c => c.ProcessCode).NotEmpty().MaximumLength(20);
        RuleFor(c => c.BusinessRefType).NotEmpty().MaximumLength(30);
        RuleFor(c => c.BusinessRefId).NotEmpty();
    }
}

public sealed class CaseNoteValidator : AbstractValidator<AddCaseNote>
{
    public CaseNoteValidator() => RuleFor(c => c.Note).NotEmpty().MaximumLength(10_000);
}

internal static class ProcessAccess
{
    public static async Task<Result<ProcessInstance>> Load(IProcessRepository processes, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var process = await processes.Get(id, cancellationToken);
        if (process is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The process was not found.");
        }

        return expectedVersion is { } expected && process.RowVersion != expected
            ? Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The process has changed since it was read.")
            : process;
    }

    public static Guid Actor(IExecutionContext execution) => Guid.TryParse(execution.ActorId, out var id) ? id : Guid.Empty;
}

internal sealed class StartProcessHandler(IProcessRepository processes, IWorkflowConfiguration configuration, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<StartProcess, Guid>
{
    public async Task<Result<Guid>> Handle(StartProcess command, CancellationToken cancellationToken)
    {
        var definition = await configuration.Definition(command.ProcessCode, null, businessDate.Today, cancellationToken);
        if (definition.IsFailure)
        {
            return definition.Error;
        }

        var route = await configuration.Routing(businessDate.Today, clock.UtcNow, cancellationToken);
        var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), definition.Value, command.BusinessRefType, command.BusinessRefId, route, clock.UtcNow);
        if (process.IsFailure)
        {
            return process.Error;
        }

        processes.Add(process.Value);
        audit.Record(new AuditEntry("WORKFLOW", "StartProcess", nameof(ProcessInstance), process.Value.Id.ToString(),
            new Dictionary<string, string?> { ["process"] = $"{command.ProcessCode}@{definition.Value.Version}" }));
        return process.Value.Id;
    }
}

internal sealed class AssignTaskHandler(IProcessRepository processes, IExecutionContext execution, IAuditTrail audit) : ICommandHandler<AssignTask, long>
{
    public async Task<Result<long>> Handle(AssignTask command, CancellationToken cancellationToken)
    {
        var process = await ProcessAccess.Load(processes, command.ProcessId, command.ExpectedVersion, cancellationToken);
        if (process.IsFailure)
        {
            return process.Error;
        }

        var assignee = command.AssigneeId ?? ProcessAccess.Actor(execution);
        if (assignee == Guid.Empty)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Tasks are assigned to identified users.");
        }

        var task = process.Value.Tasks.SingleOrDefault(t => t.Id == command.TaskId);
        audit.Record(new AuditEntry("WORKFLOW", task?.AssigneeId is null ? "AssignTask" : "ReassignTask", nameof(ProcessInstance), command.ProcessId.ToString(),
            new Dictionary<string, string?> { ["task"] = command.TaskId.ToString(), ["assignee"] = assignee.ToString() }));
        var result = process.Value.AssignTask(command.TaskId, assignee);
        return result.IsFailure ? result.Error : process.Value.RowVersion + 1;
    }
}

/// <summary>CompleteTask, after the owning domain's validation of the transition (WFL-002).</summary>
internal sealed class CompleteTaskHandler(IProcessRepository processes, IWorkflowConfiguration configuration, IEnumerable<IWorkflowTransitionValidator> validators,
    IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<CompleteTask, long>
{
    public async Task<Result<long>> Handle(CompleteTask command, CancellationToken cancellationToken)
    {
        var process = await ProcessAccess.Load(processes, command.ProcessId, command.ExpectedVersion, cancellationToken);
        if (process.IsFailure)
        {
            return process.Error;
        }

        // OQ-138: a periodic due diligence review is completed through Party (POST /party/parties/{id}/due-diligence/review); its task closes then.
        if (process.Value.ProcessCode == BuiltInProcesses.DueDiligenceReview.ProcessCode)
        {
            return Error.Conflict(WorkflowErrorCodes.TaskClosedByItsRecord,
                "This review is completed on the party's due diligence; the task closes when the party clears.");
        }

        var definition = await configuration.Definition(process.Value.ProcessCode, process.Value.ProcessVersion, businessDate.Today, cancellationToken);
        if (definition.IsFailure)
        {
            return definition.Error;
        }

        var variables = Variables(command.Variables);
        if (definition.Value.DomainValidation is { Length: > 0 } service)
        {
            var validator = validators.FirstOrDefault(v => v.ServiceCode == service);
            if (validator is null)
            {
                return Error.Validation(WorkflowErrorCodes.DomainValidationFailed,
                    $"The definition requires domain validation '{service}', which no module provides; the transition is refused (WFL-002).");
            }

            var valid = await validator.Validate(process.Value.BusinessRefType, process.Value.BusinessRefId, process.Value.DefinitionState, command.ToState, variables,
                cancellationToken);
            if (valid.IsFailure)
            {
                return valid.Error;
            }
        }

        audit.Record(new AuditEntry("WORKFLOW", "CompleteTask", nameof(ProcessInstance), command.ProcessId.ToString(),
            new Dictionary<string, string?> { ["from"] = process.Value.DefinitionState, ["to"] = command.ToState }));
        var route = await configuration.Routing(businessDate.Today, clock.UtcNow, cancellationToken);
        var result = process.Value.CompleteTask(command.TaskId, command.ToState, definition.Value, variables, route, clock.UtcNow);
        return result.IsFailure ? result.Error : process.Value.RowVersion + 1;
    }

    private static Dictionary<string, object?> Variables(JsonElement? json) =>
        json is { ValueKind: JsonValueKind.Object } value
            ? value.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, object?>();
}

internal sealed class ChangeProcessHandler(IProcessRepository processes, IClock clock, IAuditTrail audit) : ICommandHandler<ChangeProcess, long>
{
    public async Task<Result<long>> Handle(ChangeProcess command, CancellationToken cancellationToken)
    {
        var process = await ProcessAccess.Load(processes, command.ProcessId, command.ExpectedVersion, cancellationToken);
        if (process.IsFailure)
        {
            return process.Error;
        }

        audit.Record(new AuditEntry("WORKFLOW", $"{command.Action}Process", nameof(ProcessInstance), command.ProcessId.ToString()));
        var result = command.Action switch
        {
            ProcessAction.Suspend => process.Value.SuspendProcess(),
            ProcessAction.Resume => process.Value.ResumeProcess(),
            _ => process.Value.CancelProcess(clock.UtcNow),
        };
        return result.IsFailure ? result.Error : process.Value.RowVersion + 1;
    }
}

internal sealed class AddCaseNoteHandler(IProcessRepository processes, IExecutionContext execution) : ICommandHandler<AddCaseNote, long>
{
    public async Task<Result<long>> Handle(AddCaseNote command, CancellationToken cancellationToken)
    {
        var process = await ProcessAccess.Load(processes, command.ProcessId, command.ExpectedVersion, cancellationToken);
        if (process.IsFailure)
        {
            return process.Error;
        }

        process.Value.AddNote(ProcessAccess.Actor(execution), command.Note);
        return process.Value.RowVersion + 1;
    }
}

internal sealed class FireTimersHandler(IProcessRepository processes, IClock clock) : ICommandHandler<FireTimers, int>
{
    public async Task<Result<int>> Handle(FireTimers command, CancellationToken cancellationToken)
    {
        var process = await processes.Get(command.ProcessId, cancellationToken);
        if (process is null)
        {
            return 0;
        }

        var before = process.DomainEvents.Count;
        process.FireDueTimers(clock.UtcNow);
        return process.DomainEvents.Count - before;
    }
}

/// <summary>EscalateTask: the next escalation rule above the task's current level decides the target (WFL-003).</summary>
public sealed class TaskEscalation(IWorkflowConfiguration configuration, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
{
    public async Task<Result> Escalate(ProcessInstance process, Guid taskId, CancellationToken cancellationToken)
    {
        var task = process.Tasks.SingleOrDefault(t => t.Id == taskId);
        var currentLevel = task?.Outcome is { } outcome && outcome.StartsWith("ESCALATED_L", StringComparison.Ordinal) ? int.Parse(outcome[11..]) : 0;
        var next = await configuration.NextEscalation(currentLevel, businessDate.Today, cancellationToken);
        if (next is null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "No escalation rule applies above the current level.");
        }

        audit.Record(new AuditEntry("WORKFLOW", "EscalateTask", nameof(ProcessInstance), process.Id.ToString(),
            new Dictionary<string, string?> { ["task"] = taskId.ToString(), ["level"] = next.Value.Level.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        var actor = ProcessAccess.Actor(execution);
        return process.EscalateTask(taskId, next.Value.Level, next.Value.TargetRole, actor == Guid.Empty ? null : actor, clock.UtcNow);
    }
}

internal sealed class EscalateTaskHandler(IProcessRepository processes, TaskEscalation escalation) : ICommandHandler<EscalateTask, long>
{
    public async Task<Result<long>> Handle(EscalateTask command, CancellationToken cancellationToken)
    {
        var process = await ProcessAccess.Load(processes, command.ProcessId, null, cancellationToken);
        if (process.IsFailure)
        {
            return process.Error;
        }

        var result = await escalation.Escalate(process.Value, command.TaskId, cancellationToken);
        return result.IsFailure ? result.Error : process.Value.RowVersion + 1;
    }
}

internal sealed class ListProcessesHandler(IProcessQueries queries, IWorkflowConfiguration configuration, IBusinessDate businessDate)
    : IQueryHandler<ListProcesses, IReadOnlyList<ProcessView>>
{
    public async Task<Result<IReadOnlyList<ProcessView>>> Handle(ListProcesses query, CancellationToken cancellationToken)
    {
        var definitions = new Dictionary<(string, int), ProcessDefinition?>();
        var views = new List<ProcessView>();
        foreach (var view in await queries.List(query.BusinessRefType, query.BusinessRefId, query.AfterId, query.Limit, cancellationToken))
        {
            views.Add(await ProcessView.WithTransitions(view, configuration, businessDate.Today, definitions, cancellationToken));
        }

        return views;
    }
}

internal sealed class GetProcessHandler(IProcessQueries queries, IWorkflowConfiguration configuration, IBusinessDate businessDate) : IQueryHandler<GetProcess, ProcessView>
{
    public async Task<Result<ProcessView>> Handle(GetProcess query, CancellationToken cancellationToken) =>
        await queries.Get(query.ProcessId, cancellationToken) is { } view
            ? await ProcessView.WithTransitions(view, configuration, businessDate.Today, [], cancellationToken)
            : Error.NotFound(ErrorCodes.NotFound, "The process was not found.");
}

internal sealed class ListOpenTasksHandler(IProcessQueries queries) : IQueryHandler<ListOpenTasks, IReadOnlyList<OpenTaskView>>
{
    public async Task<Result<IReadOnlyList<OpenTaskView>>> Handle(ListOpenTasks query, CancellationToken cancellationToken) =>
        Result.Success(await queries.OpenTasks(query.QueueCode, query.AssigneeId, query.After, query.Limit, cancellationToken));
}

internal sealed class ProcessesWithDueTimersHandler(IProcessRepository processes) : IQueryHandler<ProcessesWithDueTimers, IReadOnlyList<Guid>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(ProcessesWithDueTimers query, CancellationToken cancellationToken) =>
        Result.Success(await processes.WithDueTimers(query.Now, 500, cancellationToken));
}

/// <summary>
/// Workflow's policy for its own SlaBreached: EscalateTask (domain-model.json), in the inbox transaction. If the task
/// was completed meanwhile, or no escalation rule applies, there is nothing to do and nothing to retry.
/// </summary>
public sealed class EscalateOnSlaBreach(IProcessRepository processes, TaskEscalation escalation) : IIntegrationEventHandler<Contracts.SlaBreached>
{
    public async Task Handle(Contracts.SlaBreached breached, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await processes.Get(breached.ProcessInstanceId, cancellationToken) is { Status: ProcessStatus.Running } process
            && process.OpenTask?.Id == breached.TaskId)
        {
            _ = await escalation.Escalate(process, breached.TaskId, cancellationToken);
        }
    }
}

/// <summary>
/// Opens a task for the target role when parked deliveries escalate (ADR-022 decision 6), on the built-in one-step
/// process PARKED_DELIVERY. One task per escalation level and kind; a repeat of the event is skipped by the inbox.
/// </summary>
public sealed class OpenParkedDeliveryTask(IProcessRepository processes, IClock clock, IAuditTrail audit)
    : IIntegrationEventHandler<Integration.Contracts.ParkedDeliveryEscalated>
{
    public Task Handle(Integration.Contracts.ParkedDeliveryEscalated escalated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), BuiltInProcesses.ParkedDelivery, Integration.Contracts.ParkedDeliveryEscalated.BusinessRefType,
            escalated.ParkedDeliveryId, _ => new TaskRouting(escalated.TargetRole, null, escalated.DueAt), clock.UtcNow);
        processes.Add(process.Value);
        audit.Record(new AuditEntry("WORKFLOW", "StartProcess", nameof(ProcessInstance), process.Value.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["process"] = $"{BuiltInProcesses.ParkedDelivery.ProcessCode}@{BuiltInProcesses.ParkedDelivery.Version}",
                ["level"] = escalated.Level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["queue"] = escalated.TargetRole,
                ["prerequisite"] = escalated.Code,
            }));
        return Task.CompletedTask;
    }
}

/// <summary>
/// F009: fraud indicators skipped on a claim open a configuration review task for the tenant's configuration owner role,
/// due in one business day, on the built-in process CONFIG_REVIEW. One open task per indicator: a later claim that
/// skips the same indicator while its review is open adds no task.
/// </summary>
public sealed class OpenConfigurationReviewTask(IProcessRepository processes, IProcessQueries queries, Integration.Contracts.IConfigurationOwner owner,
    Contracts.IBusinessTime businessTime, ITenantContext tenant, IClock clock, IAuditTrail audit)
    : IIntegrationEventHandler<ClaimsFramework.Contracts.FraudIndicatorsNotEvaluated>
{
    public const string FraudIndicatorRefType = "FRAUD_INDICATOR";

    public async Task Handle(ClaimsFramework.Contracts.FraudIndicatorsNotEvaluated skipped, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var role = await owner.Role(tenantId, cancellationToken);
        var now = clock.UtcNow;
        foreach (var indicator in skipped.Skipped.Select(s => s.IndicatorCode).Distinct(StringComparer.Ordinal))
        {
            var reference = ReferenceFor(indicator);
            if ((await queries.List(FraudIndicatorRefType, reference, null, 20, cancellationToken)).Any(p => p.Status == ProcessStatus.Running))
            {
                continue;
            }

            var due = await businessTime.Add(tenantId, null, now, "P1D", cancellationToken);
            var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), BuiltInProcesses.ConfigurationReview, FraudIndicatorRefType, reference,
                _ => new TaskRouting(role, null, due), now);
            processes.Add(process.Value);
            audit.Record(new AuditEntry("WORKFLOW", "StartProcess", nameof(ProcessInstance), process.Value.Id.ToString(),
                new Dictionary<string, string?>
                {
                    ["process"] = $"{BuiltInProcesses.ConfigurationReview.ProcessCode}@{BuiltInProcesses.ConfigurationReview.Version}",
                    ["indicator"] = indicator,
                    ["queue"] = role,
                    ["evaluation"] = skipped.EvaluationId.ToString(),
                }));
        }
    }

    /// <summary>A stable business reference for an indicator code, so its open review can be found again.</summary>
    public static Guid ReferenceFor(string indicatorCode)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{FraudIndicatorRefType}:{indicatorCode}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// A claim payment instruction was held instead of released (RESERVE_BELOW_ZERO, PAYEE_ACCOUNT_CHANGED): a task for the
/// claims role, due in one business day, on the built-in process CLAIM_PAYMENT_HELD. One open task per instruction.
/// </summary>
public sealed class OpenHeldClaimPaymentTask(IProcessRepository processes, IProcessQueries queries, Contracts.IBusinessTime businessTime, ITenantContext tenant,
    IClock clock, IAuditTrail audit) : IIntegrationEventHandler<ClaimsFramework.Contracts.ClaimPaymentReleaseHeld>
{
    public async Task Handle(ClaimsFramework.Contracts.ClaimPaymentReleaseHeld held, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if ((await queries.List(ClaimsFramework.Contracts.ClaimsApprovals.InstructionBusinessRefType, held.InstructionId, null, 20, cancellationToken))
            .Any(p => p.ProcessCode == BuiltInProcesses.HeldClaimPayment.ProcessCode && p.Status == ProcessStatus.Running))
        {
            return;
        }

        var now = clock.UtcNow;
        var due = await businessTime.Add(tenant.RequireTenantId(), null, now, "P1D", cancellationToken);
        var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), BuiltInProcesses.HeldClaimPayment, ClaimsFramework.Contracts.ClaimsApprovals.InstructionBusinessRefType,
            held.InstructionId, _ => new TaskRouting(held.TargetRole, null, due), now);
        processes.Add(process.Value);
        audit.Record(new AuditEntry("WORKFLOW", "StartProcess", nameof(ProcessInstance), process.Value.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["process"] = $"{BuiltInProcesses.HeldClaimPayment.ProcessCode}@{BuiltInProcesses.HeldClaimPayment.Version}",
                ["claim"] = held.ClaimNumber,
                ["reason"] = held.Reason,
                ["queue"] = held.TargetRole,
            }));
    }
}

/// <summary>
/// A party registered through a portal matched an existing one (external-user access §6): a review task for the party
/// administrators, due in one business day, on the built-in process PARTY_MATCH_REVIEW, about the new party. Neither
/// party's personal details are in the task; the reviewer opens both.
/// </summary>
public sealed class OpenPartyMatchReviewTask(IProcessRepository processes, IProcessQueries queries, Contracts.IBusinessTime businessTime, ITenantContext tenant,
    IClock clock, IAuditTrail audit) : IIntegrationEventHandler<Party.Contracts.PartyMatchReviewRequested>
{
    public const string BusinessRefType = "PARTY";

    public const string Queue = "PARTY_REVIEW";

    public async Task Handle(Party.Contracts.PartyMatchReviewRequested requested, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if ((await queries.List(BusinessRefType, requested.PartyId, null, 20, cancellationToken))
            .Any(p => p.ProcessCode == BuiltInProcesses.PartyMatchReview.ProcessCode && p.Status == ProcessStatus.Running))
        {
            return;
        }

        var now = clock.UtcNow;
        var due = await businessTime.Add(tenant.RequireTenantId(), null, now, "P1D", cancellationToken);
        var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), BuiltInProcesses.PartyMatchReview, BusinessRefType, requested.PartyId,
            _ => new TaskRouting(Queue, null, due), now);
        processes.Add(process.Value);
        audit.Record(new AuditEntry("WORKFLOW", "StartProcess", nameof(ProcessInstance), process.Value.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["process"] = $"{BuiltInProcesses.PartyMatchReview.ProcessCode}@{BuiltInProcesses.PartyMatchReview.Version}",
                ["party"] = requested.PartyId.ToString(),
                ["matched"] = requested.MatchedPartyId.ToString(),
            }));
    }
}

/// <summary>
/// Closes a party-match review task once its resolution is approved (external-user access §6): the task moves to
/// REVIEWED and the process completes. A task staff already completed is left as it is.
/// </summary>
public sealed class ClosePartyMatchReviewTask(IProcessRepository processes, IProcessQueries queries, IClock clock, IAuditTrail audit)
    : IIntegrationEventHandler<Party.Contracts.PartyMatchResolved>
{
    public async Task Handle(Party.Contracts.PartyMatchResolved resolved, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var running in (await queries.List(OpenPartyMatchReviewTask.BusinessRefType, resolved.PartyId, null, 20, cancellationToken))
                 .Where(p => p.ProcessCode == BuiltInProcesses.PartyMatchReview.ProcessCode && p.Status == ProcessStatus.Running))
        {
            if (await processes.Get(running.Id, cancellationToken) is not { OpenTask: { } task } process)
            {
                continue;
            }

            var completed = process.CompleteTask(task.Id, "REVIEWED", BuiltInProcesses.PartyMatchReview, new Dictionary<string, object?>(),
                _ => new TaskRouting(OpenPartyMatchReviewTask.Queue, null, clock.UtcNow), clock.UtcNow);
            if (completed.IsFailure)
            {
                throw new InvalidOperationException($"The review of party {resolved.PartyId} cannot be closed: {completed.Error.Code}");
            }

            audit.Record(new AuditEntry("WORKFLOW", "CompleteTask", nameof(ProcessInstance), process.Id.ToString(),
                new Dictionary<string, string?> { ["resolution"] = resolved.Resolution }));
        }
    }
}

/// <summary>
/// Due diligence work for compliance (addenda/due-diligence.md §4.3): a hit to review (DUE_DILIGENCE_HIT_REVIEW), a true
/// sanctions match to report and decide (the compliance case, DUE_DILIGENCE_SANCTIONS) and a review once a cleared party
/// expires (DUE_DILIGENCE_REVIEW), each on the party, due in one business day, in the COMPLIANCE queue, one open per party.
/// Each closes when Party says its cause is gone. The task carries the party only; the reviewer opens its due diligence.
/// </summary>
public sealed class DueDiligenceTasks(IProcessRepository processes, IProcessQueries queries, Contracts.IBusinessTime businessTime, ITenantContext tenant,
    IClock clock, IAuditTrail audit) :
    IIntegrationEventHandler<Party.Contracts.DueDiligenceReferred>,
    IIntegrationEventHandler<Party.Contracts.DueDiligenceBlocked>,
    IIntegrationEventHandler<Party.Contracts.DueDiligenceExpired>,
    IIntegrationEventHandler<Party.Contracts.DueDiligenceChanged>
{
    public const string Queue = "COMPLIANCE";

    public Task Handle(Party.Contracts.DueDiligenceReferred referred, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Open(BuiltInProcesses.DueDiligenceHitReview, referred.PartyId, cancellationToken);

    public async Task Handle(Party.Contracts.DueDiligenceBlocked blocked, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        await Close(BuiltInProcesses.DueDiligenceHitReview, blocked.PartyId, cancellationToken);
        await Open(BuiltInProcesses.DueDiligenceSanctionsMatch, blocked.PartyId, cancellationToken);
    }

    public Task Handle(Party.Contracts.DueDiligenceExpired expired, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Open(BuiltInProcesses.DueDiligenceReview, expired.PartyId, cancellationToken);

    public async Task Handle(Party.Contracts.DueDiligenceChanged changed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (changed.Status != "REFERRED")
        {
            await Close(BuiltInProcesses.DueDiligenceHitReview, changed.PartyId, cancellationToken);
        }

        if (changed.Status != "BLOCKED")
        {
            await Close(BuiltInProcesses.DueDiligenceSanctionsMatch, changed.PartyId, cancellationToken);
        }

        if (changed.Status == "CLEARED")
        {
            await Close(BuiltInProcesses.DueDiligenceReview, changed.PartyId, cancellationToken);
        }
    }

    private async Task Open(ProcessDefinition definition, Guid partyId, CancellationToken cancellationToken)
    {
        if ((await queries.List(OpenPartyMatchReviewTask.BusinessRefType, partyId, null, 20, cancellationToken))
            .Any(p => p.ProcessCode == definition.ProcessCode && p.Status == ProcessStatus.Running))
        {
            return;
        }

        var now = clock.UtcNow;
        var due = await businessTime.Add(tenant.RequireTenantId(), null, now, "P1D", cancellationToken);
        var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), definition, OpenPartyMatchReviewTask.BusinessRefType, partyId,
            _ => new TaskRouting(Queue, null, due), now);
        processes.Add(process.Value);
        audit.Record(new AuditEntry("WORKFLOW", "StartProcess", nameof(ProcessInstance), process.Value.Id.ToString(),
            new Dictionary<string, string?> { ["process"] = $"{definition.ProcessCode}@{definition.Version}", ["party"] = partyId.ToString() }));
    }

    private async Task Close(ProcessDefinition definition, Guid partyId, CancellationToken cancellationToken)
    {
        foreach (var running in (await queries.List(OpenPartyMatchReviewTask.BusinessRefType, partyId, null, 20, cancellationToken))
                 .Where(p => p.ProcessCode == definition.ProcessCode && p.Status == ProcessStatus.Running))
        {
            if (await processes.Get(running.Id, cancellationToken) is not { OpenTask: { } task } process)
            {
                continue;
            }

            var completed = process.CompleteTask(task.Id, definition.States[^1], definition, new Dictionary<string, object?>(),
                _ => new TaskRouting(Queue, null, clock.UtcNow), clock.UtcNow);
            if (completed.IsFailure)
            {
                throw new InvalidOperationException($"The {definition.ProcessCode} task of party {partyId} cannot be closed: {completed.Error.Code}");
            }

            audit.Record(new AuditEntry("WORKFLOW", "CompleteTask", nameof(ProcessInstance), process.Id.ToString(),
                new Dictionary<string, string?> { ["process"] = definition.ProcessCode }));
        }
    }
}

/// <summary>
/// F006: a membership suspended for unpaid contributions reached the end of its notice, so a task on the built-in process
/// HEALTH_ARREARS_TERMINATION about the membership (HEALTH_MEMBERSHIP), due in one business day, asks a person in the
/// HEALTH_MEMBERSHIP queue whether to terminate it; termination is never automatic. Paid arrears close it. One task is open per membership.
/// </summary>
public sealed class HealthArrearsTerminationTasks(IProcessRepository processes, IProcessQueries queries, Contracts.IBusinessTime businessTime, ITenantContext tenant,
    IClock clock, IAuditTrail audit) :
    IIntegrationEventHandler<Health.Contracts.MembershipTerminationDue>,
    IIntegrationEventHandler<Health.Contracts.MembershipArrearsCleared>
{
    public const string Queue = "HEALTH_MEMBERSHIP";

    public async Task Handle(Health.Contracts.MembershipTerminationDue due, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await Running(due.MembershipId, cancellationToken) is not [])
        {
            return;
        }

        var now = clock.UtcNow;
        var deadline = await businessTime.Add(tenant.RequireTenantId(), null, now, "P1D", cancellationToken);
        var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), BuiltInProcesses.HealthArrearsTermination, Health.Contracts.HealthMembershipTasks.BusinessRefType,
            due.MembershipId, _ => new TaskRouting(Queue, null, deadline), now);
        processes.Add(process.Value);
        audit.Record(new AuditEntry("WORKFLOW", "StartProcess", nameof(ProcessInstance), process.Value.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["process"] = $"{BuiltInProcesses.HealthArrearsTermination.ProcessCode}@{BuiltInProcesses.HealthArrearsTermination.Version}",
                ["membership"] = due.MembershipId.ToString(),
            }));
    }

    public async Task Handle(Health.Contracts.MembershipArrearsCleared cleared, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var definition = BuiltInProcesses.HealthArrearsTermination;
        foreach (var running in await Running(cleared.MembershipId, cancellationToken))
        {
            if (await processes.Get(running, cancellationToken) is not { OpenTask: { } task } process)
            {
                continue;
            }

            var completed = process.CompleteTask(task.Id, definition.States[^1], definition, new Dictionary<string, object?>(),
                _ => new TaskRouting(Queue, null, clock.UtcNow), clock.UtcNow);
            if (completed.IsFailure)
            {
                throw new InvalidOperationException($"The termination task of membership {cleared.MembershipId} cannot be closed: {completed.Error.Code}");
            }

            audit.Record(new AuditEntry("WORKFLOW", "CompleteTask", nameof(ProcessInstance), process.Id.ToString(),
                new Dictionary<string, string?> { ["process"] = definition.ProcessCode, ["why"] = "ARREARS_PAID" }));
        }
    }

    private async Task<IReadOnlyList<Guid>> Running(Guid membershipId, CancellationToken cancellationToken) =>
        (await queries.List(Health.Contracts.HealthMembershipTasks.BusinessRefType, membershipId, null, 20, cancellationToken))
        .Where(p => p.ProcessCode == BuiltInProcesses.HealthArrearsTermination.ProcessCode && p.Status == ProcessStatus.Running).Select(p => p.Id).ToList();
}

/// <summary>
/// A zone and peril reached its aggregation alert level (GAP-051, F062 §1): a review task for the alert's target role,
/// due in one business day, on the built-in process AGGREGATION_REVIEW about the zone and peril (AGGREGATION_ZONE). One
/// task is open per zone and peril; later alerts while it is open add none. An alert without a target role opens none.
/// </summary>
public sealed class OpenAggregationReviewTask(IProcessRepository processes, IProcessQueries queries, Contracts.IBusinessTime businessTime, ITenantContext tenant,
    IClock clock, IAuditTrail audit) : IIntegrationEventHandler<General.Contracts.AggregationLimitApproached>
{
    public async Task Handle(General.Contracts.AggregationLimitApproached approached, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (approached.TargetRole is not { } role)
        {
            return;
        }

        var reference = General.Contracts.AggregationZones.Reference(approached.Zone, approached.Peril);
        if ((await queries.List(General.Contracts.AggregationZones.BusinessRefType, reference, null, 20, cancellationToken))
            .Any(p => p.ProcessCode == BuiltInProcesses.AggregationReview.ProcessCode && p.Status == ProcessStatus.Running))
        {
            return;
        }

        var now = clock.UtcNow;
        var due = await businessTime.Add(tenant.RequireTenantId(), null, now, "P1D", cancellationToken);
        var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), BuiltInProcesses.AggregationReview, General.Contracts.AggregationZones.BusinessRefType,
            reference, _ => new TaskRouting(role, null, due), now);
        processes.Add(process.Value);
        audit.Record(new AuditEntry("WORKFLOW", "StartProcess", nameof(ProcessInstance), process.Value.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["process"] = $"{BuiltInProcesses.AggregationReview.ProcessCode}@{BuiltInProcesses.AggregationReview.Version}",
                ["zone"] = approached.Zone,
                ["peril"] = approached.Peril,
                ["alert"] = approached.AlertId?.ToString(),
                ["queue"] = role,
            }));
    }
}

/// <summary>
/// A General quote was referred (§7.4): a referral task on the built-in process GENERAL_REFERRAL about the quote, due in
/// one business day, in the queue of the role whose underwriting authority (rules.cfg_authority_matrices) covers the
/// quote's product and total sum insured: the smallest such authority, so the most junior who may decide it. Any role
/// with a covering authority may still decide it. With no role covering it, the task goes to the queue configured for
/// its task type (else the default queue), and onboarding readiness lists the product.
/// </summary>
public sealed class OpenGeneralReferralTask(IProcessRepository processes, IProcessQueries queries, IWorkflowConfiguration configuration,
    Rules.Contracts.IUnderwritingAuthorities authorities, Contracts.IBusinessTime businessTime, ITenantContext tenant, IBusinessDate businessDate, IClock clock,
    IAuditTrail audit) : IIntegrationEventHandler<General.Contracts.GeneralQuoteReferred>
{
    public async Task Handle(General.Contracts.GeneralQuoteReferred referred, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if ((await queries.List(General.Contracts.GeneralReferrals.BusinessRefType, referred.QuoteId, null, 20, cancellationToken))
            .Any(p => p.ProcessCode == BuiltInProcesses.GeneralReferral.ProcessCode && p.Status == ProcessStatus.Running))
        {
            return;
        }

        var tenantId = tenant.RequireTenantId();
        var now = clock.UtcNow;
        var role = Rules.Contracts.ReferralRouting.RouteTo(await authorities.ForProduct(tenantId, referred.ProductCode, businessDate.Today, cancellationToken),
            referred.SumInsured);
        var due = await businessTime.Add(tenantId, null, now, "P1D", cancellationToken);
        var routing = role is null ? await configuration.Routing(businessDate.Today, now, cancellationToken) : null;
        var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), BuiltInProcesses.GeneralReferral, General.Contracts.GeneralReferrals.BusinessRefType,
            referred.QuoteId, taskType => role is null ? routing!(taskType) : new TaskRouting(role, null, due), now);
        processes.Add(process.Value);
        audit.Record(new AuditEntry("WORKFLOW", "StartProcess", nameof(ProcessInstance), process.Value.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["process"] = $"{BuiltInProcesses.GeneralReferral.ProcessCode}@{BuiltInProcesses.GeneralReferral.Version}",
                ["quote"] = $"{referred.QuoteNumber} v{referred.QuoteVersion}",
                ["reason"] = referred.ReasonCode,
                ["queue"] = role,
            }));
    }
}

/// <summary>
/// Wave 4 §4.2: a renewal review referred a renewal, or found it ineligible. A task on the built-in process
/// GENERAL_RENEWAL_REVIEW about the renewal (GENERAL_RENEWAL), due in one business day, goes to the role whose
/// underwriting authority covers the product and sum insured, as a quote referral does. With no such role it goes to the
/// task type's configured queue. The underwriter's decision closes it. One task is open per renewal.
/// </summary>
public sealed class GeneralRenewalReviewTasks(IProcessRepository processes, IProcessQueries queries, IWorkflowConfiguration configuration,
    Rules.Contracts.IUnderwritingAuthorities authorities, Contracts.IBusinessTime businessTime, ITenantContext tenant, IBusinessDate businessDate, IClock clock,
    IAuditTrail audit) :
    IIntegrationEventHandler<General.Contracts.GeneralRenewalReferred>,
    IIntegrationEventHandler<General.Contracts.GeneralRenewalNotEligible>,
    IIntegrationEventHandler<General.Contracts.GeneralRenewalDecided>
{
    public Task Handle(General.Contracts.GeneralRenewalReferred referred, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Open(referred.RenewalId, referred.PolicyNumber, referred.ProductCode, referred.SumInsured, "REFERRED", referred.Reason, cancellationToken);

    public Task Handle(General.Contracts.GeneralRenewalNotEligible ineligible, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Open(ineligible.RenewalId, ineligible.PolicyNumber, ineligible.ProductCode, ineligible.SumInsured, "NOT_ELIGIBLE", ineligible.Reason, cancellationToken);

    public async Task Handle(General.Contracts.GeneralRenewalDecided decided, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var definition = BuiltInProcesses.GeneralRenewalReview;
        foreach (var running in await Running(decided.RenewalId, cancellationToken))
        {
            if (await processes.Get(running, cancellationToken) is not { OpenTask: { } task } process)
            {
                continue;
            }

            var completed = process.CompleteTask(task.Id, definition.States[^1], definition, new Dictionary<string, object?>(),
                _ => new TaskRouting(task.QueueCode, null, clock.UtcNow), clock.UtcNow);
            if (completed.IsFailure)
            {
                throw new InvalidOperationException($"The renewal task of {decided.PolicyNumber} cannot be closed: {completed.Error.Code}");
            }

            audit.Record(new AuditEntry("WORKFLOW", "CompleteTask", nameof(ProcessInstance), process.Id.ToString(),
                new Dictionary<string, string?> { ["decision"] = decided.DecisionCode, ["offered"] = decided.Offered.ToString() }));
        }
    }

    private async Task Open(Guid renewalId, string policyNumber, string productCode, decimal sumInsured, string why, string reason, CancellationToken cancellationToken)
    {
        if ((await Running(renewalId, cancellationToken)).Count > 0)
        {
            return;
        }

        var tenantId = tenant.RequireTenantId();
        var now = clock.UtcNow;
        var role = Rules.Contracts.ReferralRouting.RouteTo(await authorities.ForProduct(tenantId, productCode, businessDate.Today, cancellationToken), sumInsured);
        var due = await businessTime.Add(tenantId, null, now, "P1D", cancellationToken);
        var routing = role is null ? await configuration.Routing(businessDate.Today, now, cancellationToken) : null;
        var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), BuiltInProcesses.GeneralRenewalReview, General.Contracts.GeneralRenewals.BusinessRefType, renewalId,
            taskType => role is null ? routing!(taskType) : new TaskRouting(role, null, due), now);
        processes.Add(process.Value);
        audit.Record(new AuditEntry("WORKFLOW", "StartProcess", nameof(ProcessInstance), process.Value.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["process"] = $"{BuiltInProcesses.GeneralRenewalReview.ProcessCode}@{BuiltInProcesses.GeneralRenewalReview.Version}",
                ["policy"] = policyNumber,
                ["why"] = why,
                ["reason"] = reason.Length > 200 ? reason[..200] : reason,
                ["queue"] = role,
            }));
    }

    private async Task<IReadOnlyList<Guid>> Running(Guid renewalId, CancellationToken cancellationToken) =>
        (await queries.List(General.Contracts.GeneralRenewals.BusinessRefType, renewalId, null, 20, cancellationToken))
        .Where(p => p.ProcessCode == BuiltInProcesses.GeneralRenewalReview.ProcessCode && p.Status == ProcessStatus.Running).Select(p => p.Id).ToList();
}

/// <summary>Closes a General quote's referral task once the referral is decided: the task moves to DECIDED, and the audit records the decision.</summary>
public sealed class CloseGeneralReferralTask(IProcessRepository processes, IProcessQueries queries, IClock clock, IAuditTrail audit)
    : IIntegrationEventHandler<General.Contracts.GeneralQuoteReferralDecided>
{
    public async Task Handle(General.Contracts.GeneralQuoteReferralDecided decided, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var running in (await queries.List(General.Contracts.GeneralReferrals.BusinessRefType, decided.QuoteId, null, 20, cancellationToken))
                 .Where(p => p.ProcessCode == BuiltInProcesses.GeneralReferral.ProcessCode && p.Status == ProcessStatus.Running))
        {
            if (await processes.Get(running.Id, cancellationToken) is not { OpenTask: { } task } process)
            {
                continue;
            }

            var outcome = decided.Approved ? "APPROVED" : "DECLINED";
            var completed = process.CompleteTask(task.Id, "DECIDED", BuiltInProcesses.GeneralReferral, new Dictionary<string, object?>(),
                _ => new TaskRouting(task.QueueCode, null, clock.UtcNow), clock.UtcNow);
            if (completed.IsFailure)
            {
                throw new InvalidOperationException($"The referral task of quote {decided.QuoteNumber} v{decided.QuoteVersion} cannot be closed: {completed.Error.Code}");
            }

            audit.Record(new AuditEntry("WORKFLOW", "CompleteTask", nameof(ProcessInstance), process.Id.ToString(),
                new Dictionary<string, string?> { ["decision"] = decided.DecisionCode, ["outcome"] = outcome }));
        }
    }
}
