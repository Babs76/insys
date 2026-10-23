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

public sealed record ProcessView(Guid Id, string ProcessCode, int ProcessVersion, string BusinessRefType, Guid BusinessRefId, ProcessStatus Status, string State,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, IReadOnlyList<TaskView> Tasks, IReadOnlyList<CaseNoteView> Notes, long RowVersion);

public interface IProcessQueries
{
    Task<IReadOnlyList<ProcessView>> List(string? businessRefType, Guid? businessRefId, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<ProcessView?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<OpenTaskView>> OpenTasks(string? queueCode, Guid? assigneeId, int limit, CancellationToken cancellationToken);
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

public sealed record ListOpenTasks(string? QueueCode, Guid? AssigneeId, int Limit) : IQuery<IReadOnlyList<OpenTaskView>>;

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

internal sealed class StartProcessHandler(IProcessRepository processes, IWorkflowConfiguration configuration, IClock clock, IAuditTrail audit)
    : ICommandHandler<StartProcess, Guid>
{
    public async Task<Result<Guid>> Handle(StartProcess command, CancellationToken cancellationToken)
    {
        var definition = await configuration.Definition(command.ProcessCode, null, clock.UtcToday, cancellationToken);
        if (definition.IsFailure)
        {
            return definition.Error;
        }

        var route = await configuration.Routing(clock.UtcToday, clock.UtcNow, cancellationToken);
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
    IClock clock, IAuditTrail audit) : ICommandHandler<CompleteTask, long>
{
    public async Task<Result<long>> Handle(CompleteTask command, CancellationToken cancellationToken)
    {
        var process = await ProcessAccess.Load(processes, command.ProcessId, command.ExpectedVersion, cancellationToken);
        if (process.IsFailure)
        {
            return process.Error;
        }

        var definition = await configuration.Definition(process.Value.ProcessCode, process.Value.ProcessVersion, clock.UtcToday, cancellationToken);
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
        var route = await configuration.Routing(clock.UtcToday, clock.UtcNow, cancellationToken);
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
public sealed class TaskEscalation(IWorkflowConfiguration configuration, IExecutionContext execution, IClock clock, IAuditTrail audit)
{
    public async Task<Result> Escalate(ProcessInstance process, Guid taskId, CancellationToken cancellationToken)
    {
        var task = process.Tasks.SingleOrDefault(t => t.Id == taskId);
        var currentLevel = task?.Outcome is { } outcome && outcome.StartsWith("ESCALATED_L", StringComparison.Ordinal) ? int.Parse(outcome[11..]) : 0;
        var next = await configuration.NextEscalation(currentLevel, clock.UtcToday, cancellationToken);
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

internal sealed class ListProcessesHandler(IProcessQueries queries) : IQueryHandler<ListProcesses, IReadOnlyList<ProcessView>>
{
    public async Task<Result<IReadOnlyList<ProcessView>>> Handle(ListProcesses query, CancellationToken cancellationToken) =>
        Result.Success(await queries.List(query.BusinessRefType, query.BusinessRefId, query.AfterId, query.Limit, cancellationToken));
}

internal sealed class GetProcessHandler(IProcessQueries queries) : IQueryHandler<GetProcess, ProcessView>
{
    public async Task<Result<ProcessView>> Handle(GetProcess query, CancellationToken cancellationToken) =>
        await queries.Get(query.ProcessId, cancellationToken) is { } view ? view : Error.NotFound(ErrorCodes.NotFound, "The process was not found.");
}

internal sealed class ListOpenTasksHandler(IProcessQueries queries) : IQueryHandler<ListOpenTasks, IReadOnlyList<OpenTaskView>>
{
    public async Task<Result<IReadOnlyList<OpenTaskView>>> Handle(ListOpenTasks query, CancellationToken cancellationToken) =>
        Result.Success(await queries.OpenTasks(query.QueueCode, query.AssigneeId, query.Limit, cancellationToken));
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
