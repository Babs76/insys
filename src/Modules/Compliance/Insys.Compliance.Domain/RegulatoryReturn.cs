/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Compliance
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
using Insys.BuildingBlocks.Domain;

namespace Insys.Compliance.Domain;

public enum ReturnStatus
{
    Prepared,
    Reviewed,
    Approved,
    Submitted,
    Accepted,
    Rejected,
}

public enum ReturnTrigger
{
    ReviewReturn,
    ApproveReturn,
    SubmitReturn,
    RegulatorAccepts,
    RegulatorRejects,
    Corrected,
}

public static class ComplianceErrorCodes
{
    [ErrorCode(403, "error.compliance.same_person")]
    public const string SamePerson = "RETURN_SAME_PERSON";

    [ErrorCode(422, "error.compliance.mapping_invalid")]
    public const string MappingInvalid = "RETURN_MAPPING_INVALID";

    [ErrorCode(409, "error.compliance.return_exists")]
    public const string ReturnExists = "RETURN_EXISTS";

    [ErrorCode(403, "error.compliance.report_not_allowed")]
    public const string ReportNotAllowed = "REPORT_NOT_ALLOWED";

    [ErrorCode(409, "error.compliance.report_not_exported")]
    public const string ReportNotExported = "REPORT_NOT_EXPORTED";
}

public static class ReturnFrequencies
{
    public const string Monthly = "MONTHLY";
    public const string Quarterly = "QUARTERLY";
    public const string Annual = "ANNUAL";

    /// <summary>Whether a return of the frequency falls due at the end of the accounting period (yyyy-MM): every month, quarter ends, or December.</summary>
    public static bool EndsWith(string frequency, string periodCode) => Month(periodCode) is { } month && frequency switch
    {
        Monthly => true,
        Quarterly => month.Month % 3 == 0,
        Annual => month.Month == 12,
        _ => false,
    };

    /// <summary>The days a return covers: the month, or the quarter or year that ends with it.</summary>
    public static (DateOnly From, DateOnly To)? Span(string frequency, string periodCode) => Month(periodCode) is { } month
        ? (frequency switch { Quarterly => month.AddMonths(-2), Annual => month.AddMonths(-11), _ => month }, month.AddMonths(1).AddDays(-1))
        : null;

    private static DateOnly? Month(string periodCode) =>
        DateOnly.TryParseExact(periodCode + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month) ? month : null;
}

public sealed record RegulatoryReturnSubmitted(Guid ReturnId, string ReturnCode, string PeriodCode, string Regulator, string? RegulatorRef, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>
/// The RegulatoryReturn aggregate (reporting.regulatory_return_run): one return per code and period, its values from
/// the definition's field mapping (OQ-064 item 1). It is reviewed, then approved by a compliance officer before it is
/// submitted; the preparer, reviewer and approver are different people (OQ-064 item 3). A rejected return is corrected
/// back to Prepared with values calculated again.
/// </summary>
public sealed class RegulatoryReturn : AggregateRoot<Guid>
{
    public static readonly StateMachine<ReturnStatus, ReturnTrigger> Lifecycle = new StateMachine<ReturnStatus, ReturnTrigger>()
        .Allow(ReturnStatus.Prepared, ReturnTrigger.ReviewReturn, ReturnStatus.Reviewed)
        .Allow(ReturnStatus.Reviewed, ReturnTrigger.ApproveReturn, ReturnStatus.Approved)
        .Allow(ReturnStatus.Approved, ReturnTrigger.SubmitReturn, ReturnStatus.Submitted)
        .Allow(ReturnStatus.Submitted, ReturnTrigger.RegulatorAccepts, ReturnStatus.Accepted)
        .Allow(ReturnStatus.Submitted, ReturnTrigger.RegulatorRejects, ReturnStatus.Rejected)
        .Allow(ReturnStatus.Rejected, ReturnTrigger.Corrected, ReturnStatus.Prepared);

    private RegulatoryReturn()
    {
    }

    public string ReturnCode { get; private set; } = "";

    public string PeriodCode { get; private set; } = "";

    public ReturnStatus Status { get; private set; }

    /// <summary>The return's values, as a JSON object of field to amount.</summary>
    public string FieldValues { get; private set; } = "{}";

    public Guid? PreparedBy { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public string? RegulatorRef { get; private set; }

    public string? RejectionReason { get; private set; }

    public Guid? DocumentId { get; private set; }

    /// <summary>Command PrepareReturn, by a person or (preparedBy null) on the period's close.</summary>
    public static RegulatoryReturn PrepareReturn(Guid id, string returnCode, string periodCode, string values, Guid? preparedBy)
    {
        var prepared = new RegulatoryReturn
        {
            Id = Guard.NotEmpty(id), ReturnCode = Guard.MaxLength(returnCode, 20), PeriodCode = Guard.MaxLength(periodCode, 7), Status = ReturnStatus.Prepared,
            FieldValues = values, PreparedBy = preparedBy,
        };
        prepared.MarkChanged();
        return prepared;
    }

    public Result ReviewReturn(Guid by)
    {
        if (by == PreparedBy)
        {
            return Error.Forbidden(ComplianceErrorCodes.SamePerson, "The reviewer is not the person who prepared the return.");
        }

        var result = Fire(ReturnTrigger.ReviewReturn);
        if (result.IsSuccess)
        {
            ReviewedBy = by;
        }

        return result;
    }

    /// <summary>Command ApproveReturn, by a compliance officer other than the preparer and the reviewer.</summary>
    public Result ApproveReturn(Guid by)
    {
        if (by == PreparedBy || by == ReviewedBy)
        {
            return Error.Forbidden(ComplianceErrorCodes.SamePerson, "The approver is neither the preparer nor the reviewer of the return.");
        }

        var result = Fire(ReturnTrigger.ApproveReturn);
        if (result.IsSuccess)
        {
            ApprovedBy = by;
        }

        return result;
    }

    /// <summary>Command SubmitReturn: sent to the regulator through its adapter.</summary>
    public Result SubmitReturn(string regulator, string? regulatorRef, DateTimeOffset now)
    {
        var result = Fire(ReturnTrigger.SubmitReturn);
        if (result.IsSuccess)
        {
            SubmittedAt = now;
            RegulatorRef = regulatorRef;
            Raise(new RegulatoryReturnSubmitted(Id, ReturnCode, PeriodCode, regulator, regulatorRef, now));
        }

        return result;
    }

    /// <summary>The regulator's answer ("Regulator accepts", "Regulator rejects" with its reason).</summary>
    public Result RecordResponse(bool accepted, string? reason)
    {
        var result = Fire(accepted ? ReturnTrigger.RegulatorAccepts : ReturnTrigger.RegulatorRejects);
        if (result.IsSuccess && !accepted)
        {
            RejectionReason = reason;
        }

        return result;
    }

    /// <summary>"Corrected": a rejected return is prepared again, with values calculated again, for review and approval anew.</summary>
    public Result Correct(string values, Guid by)
    {
        var result = Fire(ReturnTrigger.Corrected);
        if (result.IsSuccess)
        {
            FieldValues = values;
            PreparedBy = by;
            ReviewedBy = null;
            ApprovedBy = null;
        }

        return result;
    }

    private Result Fire(ReturnTrigger trigger)
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

/// <summary>
/// Entity ReportRun (reporting.report_run): an operational report run under the catalogue's access rules; insert-only.
/// Its result is kept with the run. TODO(spec): storing the result as a document needs a document category for reports (OQ-064).
/// </summary>
public sealed class ReportRun : AggregateRoot<Guid>
{
    private ReportRun()
    {
    }

    public string ReportCode { get; private set; } = "";

    public Guid RequestedBy { get; private set; }

    /// <summary>The run's parameters and result, as JSON.</summary>
    public string Parameters { get; private set; } = "{}";

    public string Status { get; private set; } = "";

    public string? Result { get; private set; }

    public Guid? DocumentId { get; private set; }

    public bool Exported { get; private set; }

    public static ReportRun Completed(Guid id, string reportCode, Guid requestedBy, string parameters, string result, bool exported)
    {
        var run = new ReportRun
        {
            Id = Guard.NotEmpty(id), ReportCode = Guard.MaxLength(reportCode, 20), RequestedBy = requestedBy, Parameters = parameters, Status = "COMPLETED",
            Result = result, Exported = exported,
        };
        run.MarkChanged();
        return run;
    }
}
