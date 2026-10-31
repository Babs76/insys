/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance
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

namespace Insys.Finance.Domain.Ledger;

public static class TaxReturnStatus
{
    public const string Prepared = "PREPARED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
}

/// <summary>
/// The TaxReturn aggregate (F094): a period's tax for a tax code, totalled from what was posted to its account, so it equals the postings. A second person approves it
/// (SRS-002), and it is exported; INSYS files nothing. Input tax is zero: suppliers' invoices belong in the tenant's finance system (OQ-155).
/// </summary>
public sealed class TaxReturn : AggregateRoot<Guid>
{
    private TaxReturn()
    {
    }

    public string ReturnNumber { get; private set; } = "";

    public string TaxCode { get; private set; } = "";

    public string AccountCode { get; private set; } = "";

    public string Currency { get; private set; } = "";

    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    public decimal OutputTotal { get; private set; }

    public decimal InputTotal { get; private set; }

    public string Status { get; private set; } = TaxReturnStatus.Prepared;

    public Guid PreparedBy { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? RejectReason { get; private set; }

    public static TaxReturn Prepare(Guid id, string number, string taxCode, string accountCode, string currency, DateOnly periodStart, DateOnly periodEnd, decimal outputTotal, Guid preparedBy)
    {
        var taxReturn = new TaxReturn
        {
            Id = Guard.NotEmpty(id), ReturnNumber = Guard.MaxLength(number, 25), TaxCode = Guard.MaxLength(taxCode, 15), AccountCode = Guard.MaxLength(accountCode, 80), Currency = currency,
            PeriodStart = periodStart, PeriodEnd = periodEnd, OutputTotal = outputTotal, PreparedBy = Guard.NotEmpty(preparedBy),
        };
        taxReturn.MarkChanged();
        return taxReturn;
    }

    public Result Approve(Guid decidedBy, DateTimeOffset now) => Decide(TaxReturnStatus.Approved, null, decidedBy, now);

    public Result Reject(string reason, Guid decidedBy, DateTimeOffset now) => Decide(TaxReturnStatus.Rejected, Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 500), decidedBy, now);

    private Result Decide(string status, string? reason, Guid decidedBy, DateTimeOffset now)
    {
        if (Status != TaxReturnStatus.Prepared)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Return {ReturnNumber} is {Status}.");
        }

        if (decidedBy == PreparedBy)
        {
            return Error.Conflict(ErrorCodes.MakerCheckerSamePerson, "The person who prepared a return cannot decide it (SRS-002).");
        }

        Status = status;
        RejectReason = reason;
        DecidedBy = decidedBy;
        DecidedAt = now;
        MarkChanged();
        return Result.Success();
    }
}
