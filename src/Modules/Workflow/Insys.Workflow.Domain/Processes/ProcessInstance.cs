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
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;
using Insys.Workflow.Domain.Approvals;

namespace Insys.Workflow.Domain.Processes;

public enum ProcessStatus
{
    Running,
    Suspended,
    Completed,
    Cancelled,
}

public enum ProcessTrigger
{
    SuspendProcess,
    ResumeProcess,
    FinalTaskDone,
    CancelProcess,
}

/// <summary>A transition of a workflow definition: from a state to another, optionally guarded by a condition (ADR-008).</summary>
public sealed record TransitionDefinition(string From, string To, Expression? Guard);

/// <summary>
/// A version of workflow.cfg_workflow_definitions, as the process engine reads it. A process keeps the version it
/// started on (WFL-001). States come in order; the first is the initial state, and a state with no outgoing
/// transition is final.
/// </summary>
public sealed record ProcessDefinition(string ProcessCode, int Version, IReadOnlyList<string> States, IReadOnlyList<TransitionDefinition> Transitions,
    string? DomainValidation)
{
    public const int MaxStateLength = 20;

    public bool IsFinal(string state) => Transitions.All(t => t.From != state);

    public Result Validate()
    {
        if (States.Count == 0)
        {
            return Error.Validation(WorkflowErrorCodes.DefinitionInvalid, "A workflow definition needs at least one state.");
        }

        // process_instance.state is varchar(30) and holds "SUSPENDED:" (or another status) before the state (OQ-048).
        if (States.FirstOrDefault(s => s.Length > MaxStateLength) is { } longState)
        {
            return Error.Validation(WorkflowErrorCodes.DefinitionInvalid, $"State '{longState}' is longer than {MaxStateLength} characters.");
        }

        var unknown = Transitions.SelectMany(t => new[] { t.From, t.To }).FirstOrDefault(s => !States.Contains(s));
        return unknown is null
            ? Result.Success()
            : Error.Validation(WorkflowErrorCodes.DefinitionInvalid, $"Transition state '{unknown}' is not one of the definition's states.");
    }

    /// <summary>The task type for a state: "Pending approval" becomes PENDING_APPROVAL.</summary>
    public static string TaskTypeFor(string state) =>
        string.Concat(state.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')).Trim('_');
}

/// <summary>
/// Processes the platform defines in code rather than in workflow.cfg_workflow_definitions: one-step work items other
/// modules ask for through events. PARKED_DELIVERY is ADR-022's escalation of a parked delivery to a role.
/// </summary>
public static class BuiltInProcesses
{
    public static readonly ProcessDefinition ParkedDelivery = new("PARKED_DELIVERY", 1, ["RESOLVE", "RESOLVED"],
        [new TransitionDefinition("RESOLVE", "RESOLVED", null)], null);

    /// <summary>A review of configuration that could not be used (F009: a fraud indicator skipped on a claim).</summary>
    public static readonly ProcessDefinition ConfigurationReview = new("CONFIG_REVIEW", 1, ["REVIEW", "REVIEWED"],
        [new TransitionDefinition("REVIEW", "REVIEWED", null)], null);

    /// <summary>An approved claim payment whose automatic release was refused: a person puts it right, then releases it.</summary>
    public static readonly ProcessDefinition HeldClaimPayment = new("CLAIM_PAYMENT_HELD", 1, ["RESOLVE_HOLD", "RESOLVED"],
        [new TransitionDefinition("RESOLVE_HOLD", "RESOLVED", null)], null);

    /// <summary>A party registered through a portal matched an existing one: staff link the two or confirm a new party (external-user access §6).</summary>
    public static readonly ProcessDefinition PartyMatchReview = new("PARTY_MATCH_REVIEW", 1, ["REVIEW_MATCH", "REVIEWED"],
        [new TransitionDefinition("REVIEW_MATCH", "REVIEWED", null)], null);

    /// <summary>A zone and peril reached its aggregation alert level (GAP-051, F062): the underwriting role reviews its exposure.</summary>
    public static readonly ProcessDefinition AggregationReview = new("AGGREGATION_REVIEW", 1, ["REVIEW_EXPOSURE", "REVIEWED"],
        [new TransitionDefinition("REVIEW_EXPOSURE", "REVIEWED", null)], null);

    /// <summary>An approval the requester asked for expired, or was withdrawn for them: they decide what to do next.</summary>
    public static readonly ProcessDefinition ApprovalFollowUp = new("APPROVAL_FOLLOW_UP", 1, ["FOLLOW_UP", "DONE"],
        [new TransitionDefinition("FOLLOW_UP", "DONE", null)], null);

    public static ProcessDefinition? Find(string processCode, int? version) =>
        new[] { ParkedDelivery, ConfigurationReview, HeldClaimPayment, PartyMatchReview, AggregationReview, ApprovalFollowUp }.FirstOrDefault(d => d.ProcessCode == processCode && (version is null || version == d.Version));
}

/// <summary>Where a new task goes and when it is due: from the task type, queue and SLA definitions in force.</summary>
public sealed record TaskRouting(string QueueCode, string? SlaCode, DateTimeOffset DueAt);

public enum TaskStatus
{
    Open,
    Completed,
    Cancelled,
}

/// <summary>Entity Task (workflow.task).</summary>
public sealed class ProcessTask : Entity<Guid>, ITenantScoped
{
    private ProcessTask()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ProcessInstanceId { get; private set; }

    public string TaskType { get; private set; } = "";

    public string QueueCode { get; private set; } = "";

    public Guid? AssigneeId { get; private set; }

    public TaskStatus Status { get; private set; }

    public string? SlaCode { get; private set; }

    public DateTimeOffset DueAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>The state the task moved the process to, or ESCALATED_L{n} / CANCELLED.</summary>
    public string? Outcome { get; private set; }

    public long RowVersion { get; private set; }

    internal static ProcessTask Open(Guid processId, string taskType, TaskRouting routing) =>
        new() { Id = Guid.CreateVersion7(), ProcessInstanceId = processId, TaskType = taskType, QueueCode = routing.QueueCode, SlaCode = routing.SlaCode, DueAt = routing.DueAt };

    internal void Assign(Guid assigneeId) => AssigneeId = assigneeId;

    internal void Complete(string outcome, DateTimeOffset now)
    {
        Status = TaskStatus.Completed;
        Outcome = outcome;
        CompletedAt = now;
    }

    internal void Cancel(DateTimeOffset now)
    {
        Status = TaskStatus.Cancelled;
        Outcome = "CANCELLED";
        CompletedAt = now;
    }

    internal void Escalate(string queueCode, int level)
    {
        QueueCode = queueCode;
        AssigneeId = null;
        Outcome = $"ESCALATED_L{level}";
    }
}

/// <summary>Entity Timer (workflow.timer): an SLA deadline for an open task.</summary>
public sealed class ProcessTimer : Entity<Guid>, ITenantScoped
{
    private ProcessTimer()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ProcessInstanceId { get; private set; }

    /// <summary>SLA. An SLA timer shares its task's id, as timer_type (varchar(20)) cannot hold a task reference (OQ-048).</summary>
    public string TimerType { get; private set; } = "";

    public DateTimeOffset FireAt { get; private set; }

    public DateTimeOffset? FiredAt { get; private set; }

    public long RowVersion { get; private set; }

    public const string SlaType = "SLA";

    public Guid TaskId => Id;

    internal static ProcessTimer Sla(Guid processId, Guid taskId, DateTimeOffset fireAt) =>
        new() { Id = taskId, ProcessInstanceId = processId, TimerType = SlaType, FireAt = fireAt };

    internal void Fire(DateTimeOffset now) => FiredAt = now;

    internal void Cancel(DateTimeOffset now) => FiredAt ??= now;
}

/// <summary>Entity CaseNote (workflow.case_note), kept against the process's business reference.</summary>
public sealed class CaseNote : Entity<Guid>, ITenantScoped
{
    private CaseNote()
    {
    }

    public Guid TenantId { get; private set; }

    public string BusinessRefType { get; private set; } = "";

    public Guid BusinessRefId { get; private set; }

    public Guid AuthorId { get; private set; }

    public string Note { get; private set; } = "";

    public long RowVersion { get; private set; }

    internal static CaseNote Write(string refType, Guid refId, Guid authorId, string note) =>
        new() { Id = Guid.CreateVersion7(), BusinessRefType = refType, BusinessRefId = refId, AuthorId = authorId, Note = note };
}

public sealed record SlaBreached(Guid ProcessInstanceId, Guid TaskId, string TaskType, string? SlaCode, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The ProcessInstance aggregate. Transitions follow the definition version the process started on (WFL-001); the
/// owning domain's validation is called before each transition (WFL-002, by the application); tasks can be
/// assigned, reassigned and escalated (WFL-003); state and timers are stored, so a process survives restarts (WFL-005).
/// </summary>
public sealed class ProcessInstance : AggregateRoot<Guid>
{
    public static readonly StateMachine<ProcessStatus, ProcessTrigger> Lifecycle = new StateMachine<ProcessStatus, ProcessTrigger>()
        .Allow(ProcessStatus.Running, ProcessTrigger.SuspendProcess, ProcessStatus.Suspended)
        .Allow(ProcessStatus.Suspended, ProcessTrigger.ResumeProcess, ProcessStatus.Running)
        .Allow(ProcessStatus.Running, ProcessTrigger.FinalTaskDone, ProcessStatus.Completed)
        .Allow(ProcessStatus.Running, ProcessTrigger.CancelProcess, ProcessStatus.Cancelled);

    private readonly List<ProcessTask> _tasks = [];
    private readonly List<ProcessTimer> _timers = [];
    private readonly List<CaseNote> _notes = [];

    private ProcessInstance()
    {
    }

    public string ProcessCode { get; private set; } = "";

    public int ProcessVersion { get; private set; }

    public string BusinessRefType { get; private set; } = "";

    public Guid BusinessRefId { get; private set; }

    /// <summary>The definition state while running; SUSPENDED:{state}, COMPLETED:{state} or CANCELLED:{state} otherwise (state is varchar(30); OQ-048).</summary>
    public string State { get; private set; } = "";

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public IReadOnlyList<ProcessTask> Tasks => _tasks;

    public IReadOnlyList<ProcessTimer> Timers => _timers;

    /// <summary>Notes written since the process was loaded; the repository stores them (case_note is keyed by business reference, not process).</summary>
    public IReadOnlyList<CaseNote> NewNotes => _notes;

    public ProcessStatus Status => State.Split(':')[0] switch
    {
        "SUSPENDED" => ProcessStatus.Suspended,
        "COMPLETED" => ProcessStatus.Completed,
        "CANCELLED" => ProcessStatus.Cancelled,
        _ => ProcessStatus.Running,
    };

    /// <summary>The definition state, whatever the status.</summary>
    public string DefinitionState => State.Contains(':', StringComparison.Ordinal) ? State[(State.IndexOf(':', StringComparison.Ordinal) + 1)..] : State;

    public ProcessTask? OpenTask => _tasks.SingleOrDefault(t => t.Status == TaskStatus.Open);

    /// <summary>Command StartProcess, at the definition's first state, with its first task.</summary>
    public static Result<ProcessInstance> StartProcess(Guid id, ProcessDefinition definition, string businessRefType, Guid businessRefId,
        Func<string, TaskRouting> route, DateTimeOffset now)
    {
        var valid = definition.Validate();
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var process = new ProcessInstance
        {
            Id = Guard.NotEmpty(id),
            ProcessCode = definition.ProcessCode,
            ProcessVersion = definition.Version,
            BusinessRefType = Guard.MaxLength(Guard.NotNullOrWhiteSpace(businessRefType), 30),
            BusinessRefId = businessRefId,
            State = definition.States[0],
            StartedAt = now,
        };
        process.OpenTaskFor(definition.States[0], route);
        process.MarkChanged();
        return process;
    }

    /// <summary>Commands AssignTask and ReassignTask.</summary>
    public Result AssignTask(Guid taskId, Guid assigneeId)
    {
        var task = OpenTaskById(taskId);
        if (task.IsFailure)
        {
            return task.Error;
        }

        task.Value.Assign(assigneeId);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// Command CompleteTask: moves the process to <paramref name="toState"/> along a transition of its definition version
    /// whose guard holds. Reaching a final state completes the process (transition "Final task done").
    /// </summary>
    public Result CompleteTask(Guid taskId, string toState, ProcessDefinition definition, IReadOnlyDictionary<string, object?> variables,
        Func<string, TaskRouting> route, DateTimeOffset now)
    {
        if (definition.ProcessCode != ProcessCode || definition.Version != ProcessVersion)
        {
            return Error.Conflict(WorkflowErrorCodes.DefinitionInvalid, "A process follows the definition version it started on (WFL-001).");
        }

        var task = OpenTaskById(taskId);
        if (task.IsFailure)
        {
            return task.Error;
        }

        var candidates = definition.Transitions.Where(t => t.From == State && t.To == toState).ToList();
        if (candidates.Count == 0)
        {
            return Error.Validation(WorkflowErrorCodes.TransitionNotDefined, $"The definition has no transition from {State} to {toState}.");
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Guard is null)
            {
                return Move(task.Value, candidate.To, definition, route, now);
            }

            var guard = candidate.Guard.Evaluate(variables);
            if (guard.IsFailure)
            {
                return guard.Error;
            }

            if (guard.Value)
            {
                return Move(task.Value, candidate.To, definition, route, now);
            }
        }

        return Error.Validation(WorkflowErrorCodes.GuardFailed, $"The condition for {State} → {toState} does not hold.");
    }

    /// <summary>
    /// Command EscalateTask: the open task moves to the escalation target's queue, unassigned.
    /// TODO(spec): escalation rules name a target role, and tasks have a queue; the role code is used as the queue (OQ-049).
    /// </summary>
    public Result EscalateTask(Guid taskId, int level, string targetQueue, Guid? authorId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(targetQueue) || targetQueue.Length > 20)
        {
            return Error.Validation(WorkflowErrorCodes.DefinitionInvalid, $"Escalation target '{targetQueue}' is not a queue code of up to 20 characters.");
        }

        var task = OpenTaskById(taskId);
        if (task.IsFailure)
        {
            return task.Error;
        }

        task.Value.Escalate(targetQueue, level);
        _notes.Add(CaseNote.Write(BusinessRefType, BusinessRefId, authorId ?? Guid.Empty, $"Task {task.Value.TaskType} escalated to level {level} ({targetQueue})."));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Fires the SLA timers that are due, raising SlaBreached for each (consumed by Workflow: EscalateTask).</summary>
    public void FireDueTimers(DateTimeOffset now)
    {
        foreach (var timer in _timers.Where(t => t.FiredAt is null && t.FireAt <= now))
        {
            timer.Fire(now);
            var task = _tasks.Single(t => t.Id == timer.TaskId);
            Raise(new SlaBreached(Id, task.Id, task.TaskType, task.SlaCode, now));
        }
    }

    public Result SuspendProcess() => ChangeStatus(ProcessTrigger.SuspendProcess, "SUSPENDED:" + State);

    public Result ResumeProcess() => ChangeStatus(ProcessTrigger.ResumeProcess, DefinitionState);

    public Result CancelProcess(DateTimeOffset now)
    {
        var result = ChangeStatus(ProcessTrigger.CancelProcess, "CANCELLED:" + State);
        if (result.IsSuccess)
        {
            OpenTask?.Cancel(now);
            foreach (var timer in _timers)
            {
                timer.Cancel(now);
            }

            EndedAt = now;
        }

        return result;
    }

    public void AddNote(Guid authorId, string note)
    {
        _notes.Add(CaseNote.Write(BusinessRefType, BusinessRefId, authorId, Guard.NotNullOrWhiteSpace(note)));
        MarkChanged();
    }

    private Result Move(ProcessTask task, string to, ProcessDefinition definition, Func<string, TaskRouting> route, DateTimeOffset now)
    {
        task.Complete(to, now);
        foreach (var timer in _timers.Where(t => t.TaskId == task.Id))
        {
            timer.Cancel(now);
        }

        State = to;
        if (definition.IsFinal(to))
        {
            var done = Lifecycle.Fire(Status, ProcessTrigger.FinalTaskDone);
            if (done.IsFailure)
            {
                return done.Error;
            }

            State = "COMPLETED:" + to;
            EndedAt = now;
        }
        else
        {
            OpenTaskFor(to, route);
        }

        MarkChanged();
        return Result.Success();
    }

    private void OpenTaskFor(string state, Func<string, TaskRouting> route)
    {
        var taskType = ProcessDefinition.TaskTypeFor(state);
        var routing = route(taskType);
        var task = ProcessTask.Open(Id, taskType, routing);
        _tasks.Add(task);
        if (routing.SlaCode is not null)
        {
            _timers.Add(ProcessTimer.Sla(Id, task.Id, routing.DueAt));
        }
    }

    private Result<ProcessTask> OpenTaskById(Guid taskId)
    {
        if (Status != ProcessStatus.Running)
        {
            return Error.Conflict(WorkflowErrorCodes.ProcessNotRunning, $"The process is {Status}.");
        }

        var task = _tasks.SingleOrDefault(t => t.Id == taskId);
        return task is null ? Error.NotFound(ErrorCodes.NotFound, "The task was not found.")
            : task.Status != TaskStatus.Open ? Error.Conflict(ErrorCodes.InvalidStateTransition, "The task is not open.")
            : task;
    }

    private Result ChangeStatus(ProcessTrigger trigger, string state)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        State = state;
        MarkChanged();
        return Result.Success();
    }
}
