/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Distribution
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

namespace Insys.Distribution.Domain.Commission;

public enum EntitlementStatus
{
    Accrued,
    Payable,
    Paid,
    ClawedBack,
}

public enum EntitlementTrigger
{
    PremiumReceived,
    StatementSettled,
    ClawBack,
}

/// <summary>A commission plan version in force (commission.cfg_commission_plans).</summary>
public sealed record CommissionPlan(string PlanCode, int Version, string Basis, decimal Rate, string ClawbackRule);

public static class CommissionBases
{
    public const string Premium = "PREMIUM";
    public const string PolicyCount = "POLICY_COUNT";
    public const string Fixed = "FIXED";
}

public static class ClawbackRules
{
    public const string None = "NONE";
    public const string Unearned = "UNEARNED";
}

/// <summary>The invoice commission is accrued on (Billing's InvoiceIssued).</summary>
public sealed record CommissionSource(string SourceDomain, Guid InvoiceId, Guid PolicyRef, string ProductCode, decimal NetAmount, string Currency, DateOnly IssueDate,
    DateOnly DueDate);

public sealed record CommissionAccrued(Guid EntitlementId, string IntermediaryCode, string SourceDomain, Guid PolicyRef, Guid InvoiceId, string PlanCode, int PlanVersion,
    decimal Amount, string Currency, DateOnly AccruedOn, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record CommissionClawedBack(Guid ClawbackId, Guid EntitlementId, string IntermediaryCode, string SourceDomain, Guid PolicyRef, decimal Amount,
    string Currency, string Reason, DateOnly ClawedBackOn, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Entity Clawback (commission.clawback): always references its entitlement, for the entitlement's full amount. One
/// already on a statement is recovered on the intermediary's next statement (<see cref="StatementId"/>).
/// </summary>
public sealed class Clawback : Entity<Guid>, ITenantScoped
{
    private Clawback()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid EntitlementId { get; private set; }

    public string Reason { get; private set; } = "";

    public decimal Amount { get; private set; }

    public Guid? StatementId { get; private set; }

    internal static Clawback Create(Guid entitlementId, string reason, decimal amount) => new()
    {
        Id = Guid.CreateVersion7(), EntitlementId = entitlementId, Reason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 20), Amount = amount,
    };

    internal void RecoverOn(Guid statementId) => StatementId = statementId;

    internal void Release() => StatementId = null;
}

/// <summary>
/// The CommissionEntitlement aggregate (commission.commission_entitlement, clawback): commission earned by an
/// intermediary on one invoice. It records the plan version and source transaction (DST-003), at the plan version in
/// force on the transaction date (DST-002); a clawback always references it.
/// </summary>
public sealed class CommissionEntitlement : AggregateRoot<Guid>
{
    public const string InvoiceSource = "INVOICE";

    public static readonly StateMachine<EntitlementStatus, EntitlementTrigger> Lifecycle = new StateMachine<EntitlementStatus, EntitlementTrigger>()
        .Allow(EntitlementStatus.Accrued, EntitlementTrigger.PremiumReceived, EntitlementStatus.Payable)
        .Allow(EntitlementStatus.Payable, EntitlementTrigger.StatementSettled, EntitlementStatus.Paid)
        .Allow(EntitlementStatus.Accrued, EntitlementTrigger.ClawBack, EntitlementStatus.ClawedBack)
        .Allow(EntitlementStatus.Payable, EntitlementTrigger.ClawBack, EntitlementStatus.ClawedBack);

    private readonly List<Clawback> _clawbacks = [];

    private CommissionEntitlement()
    {
    }

    public string IntermediaryCode { get; private set; } = "";

    public string SourceType { get; private set; } = "";

    public Guid SourceId { get; private set; }

    public string SourceDomain { get; private set; } = "";

    public Guid PolicyRef { get; private set; }

    public string ProductCode { get; private set; } = "";

    public string PlanCode { get; private set; } = "";

    public int PlanVersion { get; private set; }

    public decimal BasisAmount { get; private set; }

    public decimal Rate { get; private set; }

    /// <summary>The plan's basis the rate applies on (BR-059): PREMIUM (a percentage of the net premium), FIXED or POLICY_COUNT (an amount); null for one whose plan version is gone.</summary>
    public string? RateBasis { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "";

    public DateOnly DueDate { get; private set; }

    public EntitlementStatus Status { get; private set; }

    public Guid? StatementId { get; private set; }

    public IReadOnlyList<Clawback> Clawbacks => _clawbacks;

    public Clawback? Clawback => _clawbacks.SingleOrDefault();

    /// <summary>
    /// Command AccrueCommission (OQ-062 item 3): PREMIUM is the invoice's net amount × rate %; FIXED is the rate as an
    /// amount per invoice; POLICY_COUNT is the rate as an amount on the policy's first invoice only. Nothing is accrued
    /// for a zero amount.
    /// </summary>
    public static Result<CommissionEntitlement> AccrueCommission(Guid id, string intermediaryCode, CommissionSource source, CommissionPlan plan, bool firstForPolicy,
        DateTimeOffset now)
    {
        var (basis, amount) = plan.Basis switch
        {
            CommissionBases.Premium => (source.NetAmount, Money(source.NetAmount * plan.Rate / 100m)),
            CommissionBases.Fixed => (source.NetAmount, plan.Rate),
            CommissionBases.PolicyCount => (1m, firstForPolicy ? plan.Rate : 0m),
            _ => (0m, -1m),
        };

        if (amount < 0)
        {
            return Error.Validation(DistributionErrorCodes.PlanNotApplicable, $"Commission plan {plan.PlanCode} has an unknown basis {plan.Basis}.");
        }

        if (amount == 0)
        {
            return Error.Conflict(DistributionErrorCodes.NothingToAccrue, $"Plan {plan.PlanCode} accrues nothing on invoice {source.InvoiceId}.");
        }

        var entitlement = new CommissionEntitlement
        {
            Id = Guard.NotEmpty(id), IntermediaryCode = Guard.MaxLength(intermediaryCode, 15), SourceType = InvoiceSource, SourceId = source.InvoiceId,
            SourceDomain = Guard.MaxLength(source.SourceDomain, 10), PolicyRef = source.PolicyRef, ProductCode = Guard.MaxLength(source.ProductCode, 15),
            PlanCode = plan.PlanCode, PlanVersion = plan.Version, BasisAmount = basis, Rate = plan.Rate, RateBasis = Guard.MaxLength(plan.Basis, 15), Amount = amount, Currency = source.Currency,
            DueDate = source.DueDate, Status = EntitlementStatus.Accrued,
        };
        entitlement.Raise(new CommissionAccrued(entitlement.Id, entitlement.IntermediaryCode, entitlement.SourceDomain, entitlement.PolicyRef, source.InvoiceId,
            plan.PlanCode, plan.Version, amount, source.Currency, source.IssueDate, now));
        return entitlement;
    }

    /// <summary>Command MarkPayable ("Premium received"): the invoice was paid in full (OQ-062 item 4).</summary>
    public Result MarkPayable() => Fire(EntitlementTrigger.PremiumReceived);

    /// <summary>
    /// Command ClawBack, for the full amount, from Accrued or Payable (a paid entitlement is not clawed back:
    /// domain-model.json gives Paid no transition). One already on a statement is recovered on the next one.
    /// </summary>
    public Result<Clawback> ClawBack(string reason, DateOnly day, DateTimeOffset now)
    {
        var result = Fire(EntitlementTrigger.ClawBack);
        if (result.IsFailure)
        {
            return result.Error;
        }

        var clawback = Clawback.Create(Id, reason, Amount);
        _clawbacks.Add(clawback);
        Raise(new CommissionClawedBack(clawback.Id, Id, IntermediaryCode, SourceDomain, PolicyRef, Amount, Currency, clawback.Reason, day, now));
        return clawback;
    }

    /// <summary>Whether the clawback is recovered on a statement: the entitlement was on one already.</summary>
    public bool NeedsRecovery => Status == EntitlementStatus.ClawedBack && StatementId is not null && Clawback is { StatementId: null };

    internal void PutOn(Guid statementId)
    {
        StatementId = statementId;
        MarkChanged();
    }

    internal void TakeOff()
    {
        StatementId = null;
        MarkChanged();
    }

    internal void RecoverOn(Guid statementId)
    {
        Clawback!.RecoverOn(statementId);
        MarkChanged();
    }

    internal void ReleaseRecovery()
    {
        Clawback?.Release();
        MarkChanged();
    }

    /// <summary>"Statement settled": a payable entitlement on the paid statement is paid.</summary>
    internal void Settle()
    {
        if (Status == EntitlementStatus.Payable)
        {
            Fire(EntitlementTrigger.StatementSettled);
        }
    }

    private static decimal Money(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private Result Fire(EntitlementTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}
