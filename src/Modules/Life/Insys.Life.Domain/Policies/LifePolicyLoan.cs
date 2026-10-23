/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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

namespace Insys.Life.Domain.Policies;

/// <summary>States from domain-model.json (life › LifePolicyLoan).</summary>
public enum LoanStatus
{
    Active,
    Repaid,
    Offset,
}

public enum LoanTrigger
{
    BalanceReachesZero,
    ClaimOrSurrenderSettles,
}

/// <summary>A product's loan eligibility (life.cfg_loan_eligibility_limits); a product without one does not support loans (LIF-007).</summary>
public sealed record LoanRule(decimal MaxPctCashValue, string MinPolicyDuration);

public sealed record PolicyLoanIssued(Guid LoanId, string LoanNumber, Guid PolicyId, decimal Principal, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PolicyLoanRepaid(Guid LoanId, string LoanNumber, Guid PolicyId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity LoanTransaction (life.policy_loan_transaction). Insert-only.</summary>
public sealed class LoanTransaction : Entity<Guid>, ITenantScoped
{
    public const string Advance = "ADVANCE";
    public const string Repayment = "REPAYMENT";
    public const string OffsetType = "OFFSET";

    private LoanTransaction()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid LoanId { get; private set; }

    public string TxnType { get; private set; } = "";

    public decimal Amount { get; private set; }

    public DateOnly TxnDate { get; private set; }

    public Guid? PaymentRef { get; private set; }

    internal static LoanTransaction Create(Guid loanId, string type, decimal amount, DateOnly date, Guid? paymentRef) => new()
    {
        Id = Guid.CreateVersion7(), LoanId = loanId, TxnType = type, Amount = amount, TxnDate = date, PaymentRef = paymentRef,
    };
}

/// <summary>
/// The LifePolicyLoan aggregate (life.policy_loan, policy_loan_transaction). Issued on a policy in force whose product
/// supports loans (LIF-007), within the product's share of the cash value (§9.1 F); the balance is the sum of its
/// transactions and never negative. TODO(spec): interest accrual (the day count and capitalisation of
/// life.cfg_policy_loan_rates) and the cash value itself need actuarial definitions (OQ-052); loan disbursement has no
/// payment flow yet (OQ-061).
/// </summary>
public sealed class LifePolicyLoan : AggregateRoot<Guid>
{
    public static readonly StateMachine<LoanStatus, LoanTrigger> Lifecycle = new StateMachine<LoanStatus, LoanTrigger>()
        .Allow(LoanStatus.Active, LoanTrigger.BalanceReachesZero, LoanStatus.Repaid)
        .Allow(LoanStatus.Active, LoanTrigger.ClaimOrSurrenderSettles, LoanStatus.Offset);

    private readonly List<LoanTransaction> _transactions = [];

    private LifePolicyLoan()
    {
    }

    public string LoanNumber { get; private set; } = "";

    public Guid PolicyId { get; private set; }

    public decimal Principal { get; private set; }

    public string RateCode { get; private set; } = "";

    public decimal Balance { get; private set; }

    public LoanStatus Status { get; private set; }

    public DateOnly IssuedAt { get; private set; }

    public IReadOnlyList<LoanTransaction> Transactions => _transactions;

    /// <param name="cashValue">The policy's latest cash value (life.cash_value) on or before today, if any.</param>
    /// <param name="outstanding">The balance of the policy's other active loans.</param>
    public static Result<LifePolicyLoan> IssueLoan(Guid id, string loanNumber, LifePolicy policy, LoanRule? rule, decimal? cashValue, decimal outstanding, decimal principal,
        string rateCode, DateOnly today, DateTimeOffset now)
    {
        if (rule is null)
        {
            return Error.Validation(LifeErrorCodes.LoanNotSupported, $"{policy.ProductCode} does not support policy loans (LIF-007).");
        }

        if (policy.Status != LifePolicyStatus.InForce)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"A loan is issued on a policy in force; {policy.PolicyNumber} is {policy.Status}.");
        }

        if (IsoPeriod.Add(policy.InceptionDate, rule.MinPolicyDuration) is not { } eligibleFrom || today < eligibleFrom)
        {
            return Error.Validation(LifeErrorCodes.LoanNotSupported, $"A loan is available {rule.MinPolicyDuration} after inception.");
        }

        if (principal <= 0 || cashValue is null || outstanding + principal > Math.Round(cashValue.Value * rule.MaxPctCashValue / 100m, 2, MidpointRounding.ToZero))
        {
            return Error.Validation(LifeErrorCodes.LoanExceedsCashValue,
                cashValue is null ? "The policy has no cash value." : $"Loans may not exceed {rule.MaxPctCashValue}% of the cash value of {cashValue:0.00}.");
        }

        var loan = new LifePolicyLoan
        {
            Id = id, LoanNumber = Guard.MaxLength(loanNumber, 15), PolicyId = policy.Id, Principal = principal, RateCode = Guard.MaxLength(rateCode, 15), Balance = principal,
            Status = LoanStatus.Active, IssuedAt = today,
        };
        loan._transactions.Add(LoanTransaction.Create(id, LoanTransaction.Advance, principal, today, null));
        loan.Raise(new PolicyLoanIssued(id, loan.LoanNumber, policy.Id, principal, policy.Currency, now));
        return loan;
    }

    /// <summary>Command RecordRepayment; at a zero balance the loan is Repaid.</summary>
    public Result RecordRepayment(decimal amount, DateOnly date, Guid? paymentRef, DateTimeOffset now)
    {
        if (Status != LoanStatus.Active)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Loan {LoanNumber} is {Status}.");
        }

        if (amount <= 0 || amount > Balance)
        {
            return Error.Validation(LifeErrorCodes.RepaymentExceedsBalance, $"A repayment is above zero and at most the balance of {Balance:0.00}.");
        }

        _transactions.Add(LoanTransaction.Create(Id, LoanTransaction.Repayment, amount, date, paymentRef));
        Balance -= amount;
        MarkChanged();
        if (Balance > 0)
        {
            return Result.Success();
        }

        var result = Fire(LoanTrigger.BalanceReachesZero);
        if (result.IsSuccess)
        {
            Raise(new PolicyLoanRepaid(Id, LoanNumber, PolicyId, now));
        }

        return result;
    }

    /// <summary>Command OffsetAgainstBenefit: an approved claim (or surrender) settles the balance.</summary>
    public Result OffsetAgainstBenefit(DateOnly date)
    {
        if (Status == LoanStatus.Offset)
        {
            return Result.Success();
        }

        var result = Fire(LoanTrigger.ClaimOrSurrenderSettles);
        if (result.IsSuccess)
        {
            _transactions.Add(LoanTransaction.Create(Id, LoanTransaction.OffsetType, Balance, date, null));
            Balance = 0;
        }

        return result;
    }

    private Result Fire(LoanTrigger trigger)
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
