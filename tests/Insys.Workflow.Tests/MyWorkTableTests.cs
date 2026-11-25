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
using Insys.Workflow.Application;
using Insys.Workflow.Domain.Processes;

namespace Insys.Workflow.Tests;

/// <summary>DE-045, DE-025: the table of what follows a granted approval, and who serves a built-in process's tasks (docs/specs/addenda/my-work.md).</summary>
public sealed class MyWorkTableTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryNextStep_IsABuiltInOneStepProcess_AndOneRowPerApprovalTypeAndRecord_DE045()
    {
        foreach (var step in ApprovalNextSteps.All)
        {
            BuiltInProcesses.Find(step.Process.ProcessCode, step.Process.Version).ShouldBe(step.Process);
            step.Process.Transitions.Count.ShouldBe(1, $"{step.Process.ProcessCode}: one step, so \"done\" needs no choice");
            step.Process.ProcessCode.Length.ShouldBeLessThanOrEqualTo(20, "process_instance.process_code is varchar(20)");
        }

        ApprovalNextSteps.All.Select(s => (s.ApprovalType, s.BusinessRefType)).ShouldBeUnique();
    }

    [Fact]
    public void AQueueStep_IsServedByAFunction_AndAMakerStepExcludesNobody_DE045()
    {
        foreach (var step in ApprovalNextSteps.All)
        {
            if (step.Assignee == StepAssignee.Queue)
            {
                step.ServedBy.ShouldNotBeEmpty(step.ApprovalType);
                step.ServedBy.Except(WorkServers.For(step.Process.ProcessCode)).ShouldBeEmpty();
            }
            else
            {
                step.ExcludeMaker.ShouldBeFalse(step.ApprovalType);
            }
        }
    }

    [Theory]
    [InlineData("CLAIM_PAYMENT", true)]
    [InlineData("PAYOUT_RELEASE", true)]
    [InlineData("PROVIDER_PAYMENT_RUN", true)]
    [InlineData("REFUND", false)]
    public void AReleaseStep_ExcludesItsMaker_SRS002(string approvalType, bool excluded) =>
        ApprovalNextSteps.All.Where(s => s.ApprovalType == approvalType).ShouldAllBe(s => s.ExcludeMaker == excluded);

    [Theory]
    [InlineData("STATIC_DATA_T1", "STATIC_DATA_CHANGE")]
    [InlineData("PAYOUT_RETRY", "HEALTH_REMITTANCE")]
    [InlineData("CASE_REDRESS", "CASE_REDRESS")]
    public void ListedApprovals_HaveANextStep_DE045(string approvalType, string businessRefType) =>
        ApprovalNextSteps.For(approvalType, businessRefType).ShouldNotBeNull();

    [Theory]
    [InlineData("CREDIT_NOTE", "CREDIT_NOTE_REQUEST")]
    [InlineData("STATIC_DATA_T1", "STATIC_DATA_SETUP")]
    [InlineData("PAYOUT_RETRY", "PAYOUT_BATCH")]
    public void UnlistedApprovals_HaveNone_DE045(string approvalType, string businessRefType) =>
        ApprovalNextSteps.For(approvalType, businessRefType).ShouldBeNull();

    [Fact]
    public void AHolderOfAFunction_ServesTheProcessesItDoes_DE025()
    {
        var served = WorkServers.ServedBy(new HashSet<string>(["CLAIMS_RELEASE_INSTRUCTION", "RETRY_PARKED_DELIVERY"], StringComparer.Ordinal));
        served.ShouldBe(["CLAIM_PAYMENT_HELD", "CLAIM_PAY_RELEASE", "PARKED_DELIVERY"], ignoreOrder: true);
        WorkServers.ServedBy(new HashSet<string>(StringComparer.Ordinal)).ShouldBeEmpty();
        WorkServers.All.Keys.ShouldAllBe(code => BuiltInProcesses.Find(code, null) != null);
    }

    [Fact]
    public void AProcessFollowingAnApproval_RefusesTheExcludedPerson_SRS002()
    {
        var maker = Guid.CreateVersion7();
        var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), BuiltInProcesses.PayoutBatchRelease, "PAYOUT_BATCH", Guid.CreateVersion7(),
            _ => new TaskRouting("FINANCE_MANAGER", null, Now), Now).Value;
        process.MayBeWorkedBy(maker).ShouldBeTrue();
        var approval = Guid.CreateVersion7();
        process.Follow(approval, maker, "REFUND", "ZAR");
        (process.ApprovalId, process.PayoutPurpose, process.PayoutCurrency).ShouldBe((approval, "REFUND", "ZAR"));
        process.MayBeWorkedBy(maker).ShouldBeFalse();
        process.MayBeWorkedBy(Guid.CreateVersion7()).ShouldBeTrue();
    }

    [Fact]
    public void ARecordTypeTheProcessesUse_HasAScreen_BR005()
    {
        foreach (var type in new[] { "PARKED_DELIVERY", "PARTY", "LIFE_PROPOSAL", "GENERAL_PROPOSAL", "HEALTH_MEMBERSHIP" })
        {
            WorkServers.ScreenFor(type).ShouldNotBeNull(type);
        }
    }
}
