/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow.Tests
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
using Insys.Testing;
using Insys.Workflow.Domain.Approvals;
using Insys.Workflow.Domain.Processes;
using TaskStatus = Insys.Workflow.Domain.Processes.TaskStatus;

namespace Insys.Workflow.Tests;

public sealed class ProcessInstanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly ProcessDefinition Claim = new("CLAIM", 2, ["Registered", "Assessment", "Approved", "Declined"],
    [
        new("Registered", "Assessment", null),
        new("Assessment", "Approved", Expression.Parse("amount <= 10000").Value),
        new("Assessment", "Declined", null),
    ], "CLAIMS");

    private static TaskRouting Route(string taskType) => new("CLAIMS_Q", "SLA_" + taskType[..3], Now.AddHours(4));

    private static ProcessInstance Started() =>
        ProcessInstance.StartProcess(Guid.CreateVersion7(), Claim, "CLAIM", Guid.CreateVersion7(), Route, Now).Value;

    private static Dictionary<string, object?> Vars(decimal amount) => new() { ["amount"] = amount };

    [Fact]
    public void ProcessInstance_Lifecycle_MatchesDomainModel()
    {
        var specified = DomainModelSpec.Transitions("workflow", "ProcessInstance")
            .Select(t => t.Replace("Final task done", "FinalTaskDone", StringComparison.Ordinal)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        ProcessInstance.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList().ShouldBe(specified);
    }

    [Fact]
    public void StartProcess_OpensTheFirstTask_WithItsSlaTimer()
    {
        var process = Started();
        process.State.ShouldBe("Registered");
        process.ProcessVersion.ShouldBe(2);
        var task = process.OpenTask.ShouldNotBeNull();
        task.TaskType.ShouldBe("REGISTERED");
        task.QueueCode.ShouldBe("CLAIMS_Q");
        process.Timers.Single().TaskId.ShouldBe(task.Id);
    }

    [Fact]
    public void CompleteTask_AlongAnUndefinedTransition_IsRefused()
    {
        var process = Started();
        process.CompleteTask(process.OpenTask!.Id, "Approved", Claim, Vars(1), Route, Now).Error!.Code.ShouldBe(WorkflowErrorCodes.TransitionNotDefined);
    }

    [Fact]
    public void CompleteTask_WhoseGuardFails_IsRefused()
    {
        var process = Started();
        process.CompleteTask(process.OpenTask!.Id, "Assessment", Claim, Vars(1), Route, Now).IsSuccess.ShouldBeTrue();
        process.CompleteTask(process.OpenTask!.Id, "Approved", Claim, Vars(20_000), Route, Now).Error!.Code.ShouldBe(WorkflowErrorCodes.GuardFailed);
        process.DefinitionState.ShouldBe("Assessment");
    }

    [Fact]
    public void CompleteTask_WithAMissingGuardVariable_IsAnError_NotFalse()
    {
        var process = Started();
        process.CompleteTask(process.OpenTask!.Id, "Assessment", Claim, Vars(1), Route, Now).IsSuccess.ShouldBeTrue();
        process.CompleteTask(process.OpenTask!.Id, "Approved", Claim, new Dictionary<string, object?>(), Route, Now).Error!.Code
            .ShouldBe(ExpressionErrors.EvaluationCode);
    }

    [Fact]
    public void ReachingAFinalState_CompletesTheProcess_AndCancelsItsTimers()
    {
        var process = Started();
        process.CompleteTask(process.OpenTask!.Id, "Assessment", Claim, Vars(1), Route, Now).IsSuccess.ShouldBeTrue();
        process.CompleteTask(process.OpenTask!.Id, "Approved", Claim, Vars(500), Route, Now.AddHours(1)).IsSuccess.ShouldBeTrue();

        process.Status.ShouldBe(ProcessStatus.Completed);
        process.EndedAt.ShouldBe(Now.AddHours(1));
        process.OpenTask.ShouldBeNull();
        process.Timers.ShouldAllBe(t => t.FiredAt != null);
    }

    [Fact]
    public void Process_FollowsTheDefinitionVersionItStartedOn_WFL001()
    {
        var process = Started();
        var newer = Claim with { Version = 3 };
        process.CompleteTask(process.OpenTask!.Id, "Assessment", newer, Vars(1), Route, Now).Error!.Code.ShouldBe(WorkflowErrorCodes.DefinitionInvalid);
    }

    [Fact]
    public void DueSlaTimer_RaisesSlaBreachedOnce_WFL003()
    {
        var process = Started();
        process.FireDueTimers(Now.AddHours(3));
        process.DomainEvents.OfType<SlaBreached>().ShouldBeEmpty();

        process.FireDueTimers(Now.AddHours(5));
        process.FireDueTimers(Now.AddHours(6));
        process.DomainEvents.OfType<SlaBreached>().Single().TaskId.ShouldBe(process.OpenTask!.Id);
    }

    [Fact]
    public void AssignAndEscalate_ReassignTheTask_WFL003()
    {
        var process = Started();
        var task = process.OpenTask!;
        var clerk = Guid.CreateVersion7();
        process.AssignTask(task.Id, clerk).IsSuccess.ShouldBeTrue();
        task.AssigneeId.ShouldBe(clerk);

        process.EscalateTask(task.Id, 1, "SUPERVISORS", null, Now).IsSuccess.ShouldBeTrue();
        task.QueueCode.ShouldBe("SUPERVISORS");
        task.AssigneeId.ShouldBeNull();
        process.NewNotes.ShouldHaveSingleItem();
    }

    [Fact]
    public void SuspendedProcess_KeepsItsState_AndResumesThere()
    {
        var process = Started();
        process.SuspendProcess().IsSuccess.ShouldBeTrue();
        process.Status.ShouldBe(ProcessStatus.Suspended);
        process.DefinitionState.ShouldBe("Registered");
        process.CompleteTask(process.OpenTask!.Id, "Assessment", Claim, Vars(1), Route, Now).IsSuccess.ShouldBeFalse();

        process.ResumeProcess().IsSuccess.ShouldBeTrue();
        process.State.ShouldBe("Registered");
    }

    [Fact]
    public void CancelledProcess_CancelsItsOpenTask()
    {
        var process = Started();
        var task = process.OpenTask!;
        process.CancelProcess(Now).IsSuccess.ShouldBeTrue();
        process.Status.ShouldBe(ProcessStatus.Cancelled);
        task.Status.ShouldBe(TaskStatus.Cancelled);
    }

    [Fact]
    public void Definition_WithAnUnknownOrLongState_IsInvalid()
    {
        (Claim with { Transitions = [new("Registered", "Nowhere", null)] }).Validate().Error!.Code.ShouldBe(WorkflowErrorCodes.DefinitionInvalid);
        (Claim with { States = ["A state name that is far too long"] }).Validate().Error!.Code.ShouldBe(WorkflowErrorCodes.DefinitionInvalid);
    }

    [Fact]
    public void TaskType_IsDerivedFromTheState() => ProcessDefinition.TaskTypeFor("Pending approval").ShouldBe("PENDING_APPROVAL");
}
