/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Party.Domain.Parties;

namespace Insys.Party.Application;

// F076 (addenda/finance-and-compliance.md §4.9, OQ-158): parties. Each row goes through RegisterParty's own validator and duplicate match.

public sealed record ImportParties(IReadOnlyList<IndividualData> Rows) : ICommand<BulkJobView>;

/// <param name="Status">Only parties with this status; null for all.</param>
public sealed record RequestPartyExport([property: OneOf("ACTIVE", "DORMANT", "DECEASED")] string? Status) : ICommand<BulkJobView>;

public sealed record ApprovePartyExport(Guid Id) : ICommand<BulkJobView>;

/// <summary>The approved export as CSV: IDs, numbers, names and status; no identifier numbers. Audited with its filter and row count.</summary>
public sealed record DownloadPartyExport(Guid Id) : ICommand<string>;

public sealed record GetBulkJob(Guid Id) : IQuery<BulkJobView>;

public sealed record RejectedLine(int Line, string Reason);

public sealed record BulkJobView(Guid Id, string JobType, [property: OneOf(BulkJobStatus.Done, BulkJobStatus.Rejected, BulkJobStatus.ToApprove, BulkJobStatus.Approved)] string Status, string? Filter, int TotalCount, int AcceptedCount,
    IReadOnlyList<RejectedLine> RejectedLines, long RowVersion)
{
    public static BulkJobView From(BulkJob j) => new(j.Id, j.JobType, j.Status, j.Filter, j.TotalCount, j.AcceptedCount,
        j.RejectedLines is { } lines ? JsonSerializer.Deserialize<List<RejectedLine>>(lines, JsonSerializerOptions.Web) ?? [] : [], j.RowVersion);
}

public sealed record PartyExportRow(Guid Id, string PartyNumber, string DisplayName, string Status);

public interface IBulkJobs
{
    Task<BulkJob?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PartyExportRow>> ExportRows(string? status, CancellationToken cancellationToken);

    void Add(BulkJob job);
}

public sealed class ImportPartiesValidator : AbstractValidator<ImportParties>
{
    public const int MaxRows = 5000;

    public ImportPartiesValidator() => RuleFor(c => c.Rows).NotEmpty().Must(r => r.Count <= MaxRows).WithMessage($"An import has at most {MaxRows} rows.");
}

internal sealed class BulkJobHandlers(IBulkJobs jobs, IPartyRepository parties, IValidator<RegisterParty> rowValidator, IExecutionContext execution, IClock clock, IAuditTrail audit) :
    ICommandHandler<ImportParties, BulkJobView>,
    ICommandHandler<RequestPartyExport, BulkJobView>,
    ICommandHandler<ApprovePartyExport, BulkJobView>,
    ICommandHandler<DownloadPartyExport, string>,
    IQueryHandler<GetBulkJob, BulkJobView>
{
    private static Error NotFound => Error.NotFound(ErrorCodes.NotFound, "The job was not found.");

    public async Task<Result<BulkJobView>> Handle(ImportParties command, CancellationToken cancellationToken)
    {
        var user = Parties.User(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var rejected = new List<RejectedLine>();
        var seen = new HashSet<(string, string, DateOnly)>();
        for (var i = 0; i < command.Rows.Count; i++)
        {
            var row = command.Rows[i];
            var valid = await rowValidator.ValidateAsync(new RegisterParty(row, null), cancellationToken);
            if (!valid.IsValid)
            {
                rejected.Add(new RejectedLine(i + 1, string.Join("; ", valid.Errors.Select(e => e.ErrorMessage))));
            }
            else if (!seen.Add((row.FirstNames, row.Surname, row.BirthDate)))
            {
                rejected.Add(new RejectedLine(i + 1, "The same person appears earlier in the file."));
            }
            else if ((await parties.Candidates(row, cancellationToken)).Count > 0)
            {
                rejected.Add(new RejectedLine(i + 1, "An existing party matches this person."));
            }
        }

        // All or nothing: one bad line changes nothing, and every bad line is named. No line's content is logged or audited.
        if (rejected.Count == 0)
        {
            foreach (var row in command.Rows)
            {
                parties.Add(Domain.Parties.Party.RegisterIndividual(Guid.CreateVersion7(), row, clock.UtcNow));
            }
        }

        var job = BulkJob.Imported(Guid.CreateVersion7(), command.Rows.Count, rejected.Count == 0 ? null : JsonSerializer.Serialize(rejected, JsonSerializerOptions.Web), rejected.Count, user.Value);
        jobs.Add(job);
        audit.Record(new AuditEntry("PARTY", "ImportParties", nameof(BulkJob), job.Id.ToString(), new Dictionary<string, string?>
        {
            ["rows"] = command.Rows.Count.ToString(CultureInfo.InvariantCulture), ["rejected"] = rejected.Count.ToString(CultureInfo.InvariantCulture),
        }));
        return BulkJobView.From(job);
    }

    public async Task<Result<BulkJobView>> Handle(RequestPartyExport command, CancellationToken cancellationToken)
    {
        var user = Parties.User(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var job = BulkJob.ExportRequested(Guid.CreateVersion7(), command.Status, (await jobs.ExportRows(command.Status, cancellationToken)).Count, user.Value);
        jobs.Add(job);
        audit.Record(new AuditEntry("PARTY", "RequestPartyExport", nameof(BulkJob), job.Id.ToString(), new Dictionary<string, string?> { ["filter"] = command.Status }));
        return BulkJobView.From(job);
    }

    public async Task<Result<BulkJobView>> Handle(ApprovePartyExport command, CancellationToken cancellationToken)
    {
        var user = Parties.User(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await jobs.Get(command.Id, cancellationToken) is not { } job)
        {
            return NotFound;
        }

        var approved = job.ApproveExport(user.Value);
        if (approved.IsFailure)
        {
            return approved.Error;
        }

        audit.Record(new AuditEntry("PARTY", "ApprovePartyExport", nameof(BulkJob), job.Id.ToString()));
        return BulkJobView.From(job) with { RowVersion = job.RowVersion + 1 };
    }

    public async Task<Result<string>> Handle(DownloadPartyExport command, CancellationToken cancellationToken)
    {
        if (await jobs.Get(command.Id, cancellationToken) is not { JobType: BulkJob.Export } job)
        {
            return NotFound;
        }

        if (job.Status != BulkJobStatus.Approved)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A second person approves the export before it leaves.");
        }

        var rows = await jobs.ExportRows(job.Filter, cancellationToken);
        audit.Record(new AuditEntry("PARTY", "DownloadPartyExport", nameof(BulkJob), job.Id.ToString(), new Dictionary<string, string?>
        {
            ["filter"] = job.Filter, ["rows"] = rows.Count.ToString(CultureInfo.InvariantCulture),
        }));
        var csv = new StringBuilder("id,party_number,display_name,status\n");
        foreach (var row in rows)
        {
            csv.Append(row.Id).Append(',').Append(row.PartyNumber).Append(",\"").Append(row.DisplayName.Replace("\"", "\"\"", StringComparison.Ordinal)).Append("\",").Append(row.Status).Append('\n');
        }

        return csv.ToString();
    }

    public async Task<Result<BulkJobView>> Handle(GetBulkJob query, CancellationToken cancellationToken) =>
        await jobs.Get(query.Id, cancellationToken) is { } job ? BulkJobView.From(job) : NotFound;
}
