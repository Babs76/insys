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
using System.Globalization;
using System.Text;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Finance.Domain.Ledger;
using Insys.Integration.Contracts;

namespace Insys.Finance.Application;

/// <summary>A GL export layout in force (finance.cfg_gl_export_layouts).</summary>
public sealed record GlExportLayout(string LayoutCode, string TargetSystem, string Format, string Schedule);

/// <summary>One line of a GL export: posted entries summarised by accounting date, account and currency.</summary>
public sealed record GlExportLine(DateOnly AccountingDate, string AccountCode, string Currency, decimal Debit, decimal Credit, int Entries);

public sealed record GlExportView(Guid Id, string LayoutCode, string PeriodCode, DateOnly FromDate, DateOnly ToDate, int LineCount, GlExportStatus Status,
    string? TargetRef, string? RejectionReason, long RowVersion)
{
    public static GlExportView From(GlExportBatch b) =>
        new(b.Id, b.LayoutCode, b.PeriodCode, b.FromDate, b.ToDate, b.LineCount, b.Status, b.TargetRef, b.RejectionReason, b.RowVersion);
}

/// <summary>
/// Command ExportToGeneralLedger (F055; SRS §15.1): the posted entries of a date range within one period, summarised, sent
/// to the insurer's GL in a layout in force. A range overlapping a batch that was not rejected is refused, so no entry is
/// exported twice. TODO(spec): the layout's schedule is not run by a job yet; batches are exported on request.
/// </summary>
public sealed record ExportToGeneralLedger(string LayoutCode, string PeriodCode, DateOnly FromDate, DateOnly ToDate) : ICommand<GlExportView>;

/// <summary>Command RecordGlAcknowledgement: the GL's answer for a sent batch.</summary>
public sealed record RecordGlAcknowledgement(Guid BatchId, bool Accepted, string? Reason, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed record GetGlExport(Guid BatchId) : IQuery<GlExportView>;

public sealed record ListGlExports(string? PeriodCode, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<GlExportView>>;

/// <summary>A batch's content as sent: CSV, rebuilt from the posted entries, which never change (FIN-002).</summary>
public sealed record GetGlExportContent(Guid BatchId) : IQuery<string>;

public sealed class ExportToGeneralLedgerValidator : AbstractValidator<ExportToGeneralLedger>
{
    public ExportToGeneralLedgerValidator()
    {
        RuleFor(c => c.LayoutCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.PeriodCode).Matches(@"^\d{4}-(0[1-9]|1[0-2])$").WithMessage("periodCode is a month: yyyy-MM.");
        RuleFor(c => c.ToDate).GreaterThanOrEqualTo(c => c.FromDate);
        RuleFor(c => c).Must(c => InPeriod(c.PeriodCode, c.FromDate) && InPeriod(c.PeriodCode, c.ToDate))
            .WithMessage("fromDate and toDate must be in the period.");
    }

    private static bool InPeriod(string period, DateOnly day) => day.ToString("yyyy-MM", CultureInfo.InvariantCulture) == period;
}

public interface IGlExports
{
    Task<GlExportLayout?> Layout(string layoutCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Posted entries with an accounting date in the range, summarised by date, account and currency, in that order.</summary>
    Task<IReadOnlyList<GlExportLine>> Lines(DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken);

    Task<bool> Overlaps(string layoutCode, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken);

    Task<GlExportBatch?> Get(Guid batchId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GlExportBatch>> List(string? periodCode, Guid? afterId, int limit, CancellationToken cancellationToken);

    void Add(GlExportBatch batch);
}

public static class GlExportErrorCodes
{
    [ErrorCode(422, "error.finance.gl_layout_not_in_force")]
    public const string LayoutNotInForce = "GL_LAYOUT_NOT_IN_FORCE";

    [ErrorCode(422, "error.finance.gl_format_not_supported")]
    public const string FormatNotSupported = "GL_FORMAT_NOT_SUPPORTED";

    [ErrorCode(409, "error.finance.gl_export_overlaps")]
    public const string Overlaps = "GL_EXPORT_OVERLAPS";

    [ErrorCode(409, "error.finance.gl_export_refused")]
    public const string Refused = "GL_EXPORT_REFUSED";
}

/// <summary>The CSV of a batch: one header row, then one row per line, invariant culture.</summary>
public static class GlExportCsv
{
    public static string Of(IEnumerable<GlExportLine> lines)
    {
        var csv = new StringBuilder("accounting_date,account_code,currency,debit,credit,entries\n");
        foreach (var l in lines)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{l.AccountingDate:yyyy-MM-dd},{l.AccountCode},{l.Currency},{l.Debit:0.00},{l.Credit:0.00},{l.Entries}\n");
        }

        return csv.ToString();
    }
}

internal sealed class GlExportHandlers(IGlExports exports, IGeneralLedgerAdapter ledger, ITenantContext tenant, IBusinessDate businessDate, IAuditTrail audit) :
    ICommandHandler<ExportToGeneralLedger, GlExportView>,
    ICommandHandler<RecordGlAcknowledgement, long>,
    IQueryHandler<GetGlExport, GlExportView>,
    IQueryHandler<ListGlExports, IReadOnlyList<GlExportView>>,
    IQueryHandler<GetGlExportContent, string>
{
    public async Task<Result<GlExportView>> Handle(ExportToGeneralLedger command, CancellationToken cancellationToken)
    {
        if (await exports.Layout(command.LayoutCode, businessDate.Today, cancellationToken) is not { } layout)
        {
            return Error.Validation(GlExportErrorCodes.LayoutNotInForce, $"{command.LayoutCode} is not a GL export layout in force.");
        }

        if (layout.Format != "CSV")
        {
            // TODO(spec): the API and fixed-width formats have no field definitions in the layout table.
            return Error.Validation(GlExportErrorCodes.FormatNotSupported, $"Layout {layout.LayoutCode} is {layout.Format}; only CSV is defined.");
        }

        if (await exports.Overlaps(command.LayoutCode, command.FromDate, command.ToDate, cancellationToken))
        {
            return Error.Conflict(GlExportErrorCodes.Overlaps, "Another batch of this layout, not rejected, covers part of the range.");
        }

        var lines = await exports.Lines(command.FromDate, command.ToDate, cancellationToken);
        var sent = await ledger.SendGlExport(tenant.RequireTenantId(), layout.LayoutCode, layout.TargetSystem, command.PeriodCode, GlExportCsv.Of(lines),
            cancellationToken);
        if (!sent.Accepted)
        {
            return Error.Conflict(GlExportErrorCodes.Refused, $"The general ledger refused the batch: {sent.Error}");
        }

        var batch = GlExportBatch.Sent(layout.LayoutCode, command.PeriodCode, command.FromDate, command.ToDate, lines.Count, sent.PartnerRef);
        exports.Add(batch);
        audit.Record(new AuditEntry("FINANCE", "ExportToGeneralLedger", nameof(GlExportBatch), batch.Id.ToString(),
            new Dictionary<string, string?> { ["layout"] = layout.LayoutCode, ["period"] = command.PeriodCode, ["lines"] = lines.Count.ToString(CultureInfo.InvariantCulture) }));
        return GlExportView.From(batch);
    }

    public async Task<Result<long>> Handle(RecordGlAcknowledgement command, CancellationToken cancellationToken)
    {
        if (await exports.Get(command.BatchId, cancellationToken) is not { } batch)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The batch was not found.");
        }

        if (batch.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The batch has changed.");
        }

        audit.Record(new AuditEntry("FINANCE", command.Accepted ? "AcknowledgeGlExport" : "RejectGlExport", nameof(GlExportBatch), batch.Id.ToString()));
        var result = batch.Acknowledge(command.Accepted, command.Reason);
        return result.IsFailure ? result.Error : batch.RowVersion + 1;
    }

    public async Task<Result<GlExportView>> Handle(GetGlExport query, CancellationToken cancellationToken) =>
        await exports.Get(query.BatchId, cancellationToken) is { } batch ? GlExportView.From(batch) : Error.NotFound(ErrorCodes.NotFound, "The batch was not found.");

    public async Task<Result<IReadOnlyList<GlExportView>>> Handle(ListGlExports query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<GlExportView>>((await exports.List(query.PeriodCode, query.AfterId, query.Limit, cancellationToken)).Select(GlExportView.From).ToList());

    public async Task<Result<string>> Handle(GetGlExportContent query, CancellationToken cancellationToken) =>
        await exports.Get(query.BatchId, cancellationToken) is { } batch
            ? GlExportCsv.Of(await exports.Lines(batch.FromDate, batch.ToDate, cancellationToken))
            : Error.NotFound(ErrorCodes.NotFound, "The batch was not found.");
}
