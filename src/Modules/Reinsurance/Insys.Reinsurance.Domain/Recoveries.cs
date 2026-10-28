/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Reinsurance
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
using System.Globalization;
using System.Text;
using Insys.BuildingBlocks.Domain;

namespace Insys.Reinsurance.Domain;

public enum RecoveryStatus
{
    Identified,
    Billed,
    Settled,
}

public enum RecoveryTrigger
{
    BillRecovery,
    SettleRecovery,
}

/// <summary>A settled claim, as Reinsurance identifies recoveries on it.</summary>
public sealed record SettledClaim(string SourceDomain, Guid ClaimId, string ClaimNumber, Guid PolicyRef, decimal Paid, string Currency);

public sealed record RecoveryBilled(Guid RecoveryId, string ClaimNumber, string TreatyCode, string SourceDomain, decimal Amount, string Currency, DateOnly BilledOn,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record RecoverySettled(Guid RecoveryId, string ClaimNumber, string TreatyCode, string SourceDomain, decimal Recoverable, decimal Amount, string Currency,
    DateOnly SettledOn, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The ReinsuranceRecovery aggregate (reinsurance.recovery): what a treaty recovers on one settled claim, traceable to
/// the claim, its policy and the cession it came from, and reconcilable against what the reinsurer settles (REI-003).
/// </summary>
public sealed class ReinsuranceRecovery : AggregateRoot<Guid>
{
    public static readonly StateMachine<RecoveryStatus, RecoveryTrigger> Lifecycle = new StateMachine<RecoveryStatus, RecoveryTrigger>()
        .Allow(RecoveryStatus.Identified, RecoveryTrigger.BillRecovery, RecoveryStatus.Billed)
        .Allow(RecoveryStatus.Billed, RecoveryTrigger.SettleRecovery, RecoveryStatus.Settled);

    private ReinsuranceRecovery()
    {
    }

    public string ClaimNumber { get; private set; } = "";

    public Guid ClaimId { get; private set; }

    public string SourceDomain { get; private set; } = "";

    public Guid PolicyRef { get; private set; }

    public string TreatyCode { get; private set; } = "";

    public Guid? CessionId { get; private set; }

    public decimal PaidAmount { get; private set; }

    public decimal Recoverable { get; private set; }

    public string Currency { get; private set; } = "";

    public RecoveryStatus Status { get; private set; }

    public DateOnly? BilledOn { get; private set; }

    public decimal? SettledAmount { get; private set; }

    public DateOnly? SettledOn { get; private set; }

    /// <summary>Who billed the recovery, and who recorded its settlement (BR-049); null when a job did, or before V0087.</summary>
    public Guid? BilledBy { get; private set; }

    public Guid? SettledBy { get; private set; }

    /// <summary>What the reinsurer settled less what was recoverable; zero when reconciled, null until settled.</summary>
    public decimal? Difference => SettledAmount - Recoverable;

    /// <summary>Command IdentifyRecovery on a proportional cession: the paid amount × the cession's ceded share (OQ-063 item 3).</summary>
    public static Result<ReinsuranceRecovery> Proportional(Guid id, SettledClaim claim, Guid cessionId, string treatyCode, decimal cededShare) =>
        Identify(id, claim, treatyCode, cessionId, TreatyMath.Money(claim.Paid * cededShare));

    /// <summary>Command IdentifyRecovery under an excess of loss treaty: what the claim paid above the retention (OQ-063 item 1).</summary>
    public static Result<ReinsuranceRecovery> Excess(Guid id, SettledClaim claim, TreatyTerms treaty)
    {
        var recoverable = TreatyMath.ExcessRecoverable(treaty, claim.Paid);
        return recoverable.IsFailure ? recoverable.Error : Identify(id, claim, treaty.TreatyCode, null, recoverable.Value);
    }

    private static Result<ReinsuranceRecovery> Identify(Guid id, SettledClaim claim, string treatyCode, Guid? cessionId, decimal recoverable)
    {
        if (recoverable <= 0)
        {
            return Error.Conflict(ReinsuranceErrorCodes.NothingRecoverable, $"Nothing is recoverable on {claim.ClaimNumber} under {treatyCode}.");
        }

        var recovery = new ReinsuranceRecovery
        {
            Id = Guard.NotEmpty(id), ClaimNumber = Guard.MaxLength(claim.ClaimNumber, 25), ClaimId = claim.ClaimId, SourceDomain = Guard.MaxLength(claim.SourceDomain, 10),
            PolicyRef = claim.PolicyRef, TreatyCode = Guard.MaxLength(treatyCode, 15), CessionId = cessionId, PaidAmount = claim.Paid, Recoverable = recoverable,
            Currency = claim.Currency, Status = RecoveryStatus.Identified,
        };
        recovery.MarkChanged();
        return recovery;
    }

    /// <summary>Command BillRecovery: sent to the reinsurer; Finance posts the recoverable.</summary>
    public Result BillRecovery(DateOnly on, DateTimeOffset now, Guid? by = null)
    {
        var result = Fire(RecoveryTrigger.BillRecovery);
        if (result.IsSuccess)
        {
            BilledOn = on;
            BilledBy = by;
            Raise(new RecoveryBilled(Id, ClaimNumber, TreatyCode, SourceDomain, Recoverable, Currency, on, now));
        }

        return result;
    }

    /// <summary>Command SettleRecovery: what the reinsurer paid, which may differ from the recoverable (kept for reconciliation).</summary>
    public Result SettleRecovery(decimal amount, DateOnly on, DateTimeOffset now, Guid? by = null)
    {
        if (amount < 0 || decimal.Round(amount, 2) != amount)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A settled amount is zero or more, to two decimal places.");
        }

        var result = Fire(RecoveryTrigger.SettleRecovery);
        if (result.IsSuccess)
        {
            SettledAmount = amount;
            SettledOn = on;
            SettledBy = by;
            Raise(new RecoverySettled(Id, ClaimNumber, TreatyCode, SourceDomain, Recoverable, amount, Currency, on, now));
        }

        return result;
    }

    private Result Fire(RecoveryTrigger trigger)
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

public enum BordereauStatus
{
    Prepared,
    Sent,
    Agreed,
}

public enum BordereauTrigger
{
    SendBordereau,
    AgreeBordereau,
}

public sealed record BordereauSent(Guid BordereauId, string TreatyCode, string PeriodCode, int RowCount, string? PartnerRef, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>A bordereaux layout version in force (reinsurance.cfg_bordereaux_layouts): the fields, in order, and the period.</summary>
public sealed record BordereauLayout(string LayoutCode, string TreatyCode, IReadOnlyList<string> Fields, string Frequency);

/// <summary>
/// The Bordereau aggregate (reinsurance.bordereau): one per treaty and period, the cessions calculated in the period as
/// CSV in the layout's field order (OQ-063 item 5). TODO(spec): storing it as a document needs a document type for
/// bordereaux; the CSV is kept on the bordereau.
/// </summary>
public sealed class Bordereau : AggregateRoot<Guid>
{
    /// <summary>The cession fields a layout may name.</summary>
    public static readonly IReadOnlyDictionary<string, Func<Cession, string>> CessionFields = new Dictionary<string, Func<Cession, string>>(StringComparer.Ordinal)
    {
        ["cession_id"] = c => c.Id.ToString(),
        ["policy_ref"] = c => c.SourceId.ToString(),
        ["source_type"] = c => c.SourceType,
        ["product_code"] = c => c.ProductCode,
        ["treaty_code"] = c => c.TreatyCode,
        ["period_start"] = c => c.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["period_end"] = c => c.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["gross_sum_insured"] = c => c.GrossSumInsured.ToString("0.00", CultureInfo.InvariantCulture),
        ["gross_premium"] = c => c.GrossPremium.ToString("0.00", CultureInfo.InvariantCulture),
        ["ceded_share"] = c => c.CededShare.ToString("0.000000", CultureInfo.InvariantCulture),
        ["ceded_sum_insured"] = c => c.CededSumInsured.ToString("0.00", CultureInfo.InvariantCulture),
        ["ceded_premium"] = c => c.CededPremium.ToString("0.00", CultureInfo.InvariantCulture),
        ["currency"] = c => c.Currency,
        ["rule_code"] = c => c.RuleCode,
        ["rule_version"] = c => c.RuleVersion.ToString(CultureInfo.InvariantCulture),
        ["calculated_at"] = c => c.CalculatedAt.ToString("O", CultureInfo.InvariantCulture),
    };

    public static readonly StateMachine<BordereauStatus, BordereauTrigger> Lifecycle = new StateMachine<BordereauStatus, BordereauTrigger>()
        .Allow(BordereauStatus.Prepared, BordereauTrigger.SendBordereau, BordereauStatus.Sent)
        .Allow(BordereauStatus.Sent, BordereauTrigger.AgreeBordereau, BordereauStatus.Agreed);

    private Bordereau()
    {
    }

    public string TreatyCode { get; private set; } = "";

    public string PeriodCode { get; private set; } = "";

    public string LayoutCode { get; private set; } = "";

    public int RowCount { get; private set; }

    public BordereauStatus Status { get; private set; }

    public string Content { get; private set; } = "";

    public string? PartnerRef { get; private set; }

    public Guid? DocumentId { get; private set; }

    /// <summary>The period's first and last day: yyyy-MM for a monthly layout, yyyy-Qn for a quarterly one.</summary>
    public static Result<(DateOnly From, DateOnly To)> Period(string periodCode, string frequency)
    {
        if (frequency == "QUARTERLY" && periodCode.Length == 7 && periodCode[4..6] == "-Q" && int.TryParse(periodCode[..4], CultureInfo.InvariantCulture, out var year)
            && periodCode[6] is >= '1' and <= '4')
        {
            var from = new DateOnly(year, ((periodCode[6] - '1') * 3) + 1, 1);
            return (from, from.AddMonths(3).AddDays(-1));
        }

        if (frequency == "MONTHLY" && DateOnly.TryParseExact(periodCode + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return (month, month.AddMonths(1).AddDays(-1));
        }

        return Error.Validation(ReinsuranceErrorCodes.PeriodInvalid, $"{periodCode} is not a {frequency.ToLowerInvariant()} period (yyyy-MM or yyyy-Qn).");
    }

    /// <summary>Command PrepareBordereau: one row per cession calculated in the period, in the layout's field order.</summary>
    public static Result<Bordereau> PrepareBordereau(Guid id, BordereauLayout layout, string periodCode, IReadOnlyList<Cession> cessions)
    {
        var unknown = layout.Fields.Where(f => !CessionFields.ContainsKey(f)).ToList();
        if (unknown.Count > 0 || layout.Fields.Count == 0)
        {
            return Error.Validation(ReinsuranceErrorCodes.LayoutFieldUnknown, $"Layout {layout.LayoutCode} names unknown fields: {string.Join(", ", unknown)}.");
        }

        var csv = new StringBuilder().AppendJoin(',', layout.Fields).Append('\n');
        foreach (var cession in cessions.Where(c => c.TreatyCode == layout.TreatyCode).OrderBy(c => c.CalculatedAt).ThenBy(c => c.Id))
        {
            csv.AppendJoin(',', layout.Fields.Select(f => CessionFields[f](cession))).Append('\n');
        }

        return new Bordereau
        {
            Id = Guard.NotEmpty(id), TreatyCode = layout.TreatyCode, PeriodCode = Guard.MaxLength(periodCode, 7), LayoutCode = layout.LayoutCode,
            RowCount = cessions.Count(c => c.TreatyCode == layout.TreatyCode), Status = BordereauStatus.Prepared, Content = csv.ToString(),
        };
    }

    /// <summary>Command SendBordereau, through the reinsurer adapter.</summary>
    public Result SendBordereau(string? partnerRef, DateTimeOffset now)
    {
        var result = Fire(BordereauTrigger.SendBordereau);
        if (result.IsSuccess)
        {
            PartnerRef = partnerRef;
            Raise(new BordereauSent(Id, TreatyCode, PeriodCode, RowCount, partnerRef, now));
        }

        return result;
    }

    /// <summary>Command AgreeBordereau: the reinsurer agreed it.</summary>
    public Result AgreeBordereau() => Fire(BordereauTrigger.AgreeBordereau);

    private Result Fire(BordereauTrigger trigger)
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
