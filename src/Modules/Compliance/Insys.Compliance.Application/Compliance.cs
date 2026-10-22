using System.Globalization;
using System.Text.Json;
using FluentValidation;
using Insys.Analytics.Contracts;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Compliance.Domain;
using Insys.Finance.Contracts;
using Insys.Identity.Contracts;
using Insys.Integration.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.Compliance.Application;

public interface IComplianceRepository
{
    Task<RegulatoryReturn?> Return(Guid id, CancellationToken cancellationToken);

    Task<RegulatoryReturn?> Return(string returnCode, string periodCode, CancellationToken cancellationToken);

    Task<IReadOnlyList<RegulatoryReturn>> Returns(string? periodCode, ReturnStatus? status, CancellationToken cancellationToken);

    Task<ReportRun?> Run(Guid id, CancellationToken cancellationToken);

    void Add(RegulatoryReturn regulatoryReturn);

    void Add(ReportRun run);
}

/// <summary>A regulatory return definition in force (reporting.cfg_regulatory_return_definitions).</summary>
public sealed record ReturnDefinition(string ReturnCode, string Regulator, JsonElement FieldMapping, string Frequency);

/// <summary>A report in the catalogue (reporting.cfg_report_catalogue_and_access_rules).</summary>
public sealed record ReportDefinition(string ReportCode, string Name, IReadOnlyList<string> Roles, bool ExportAllowed);

public interface IComplianceCodes
{
    Task<ReturnDefinition?> Definition(string returnCode, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReturnDefinition>> Definitions(DateOnly day, CancellationToken cancellationToken);

    Task<ReportDefinition?> Report(string reportCode, DateOnly day, CancellationToken cancellationToken);
}

public sealed record ReturnView(Guid Id, string ReturnCode, string PeriodCode, string Status, JsonElement FieldValues, Guid? PreparedBy, Guid? ReviewedBy, Guid? ApprovedBy,
    DateTimeOffset? SubmittedAt, string? RegulatorRef, string? RejectionReason, long RowVersion)
{
    public static ReturnView From(RegulatoryReturn r) => new(r.Id, r.ReturnCode, r.PeriodCode, r.Status.ToString(), Json.Parse(r.FieldValues), r.PreparedBy, r.ReviewedBy,
        r.ApprovedBy, r.SubmittedAt, r.RegulatorRef, r.RejectionReason, r.RowVersion);
}

public sealed record ReportRunView(Guid Id, string ReportCode, Guid RequestedBy, JsonElement Parameters, string Status, JsonElement? Result, bool Exported)
{
    public static ReportRunView From(ReportRun r) => new(r.Id, r.ReportCode, r.RequestedBy, Json.Parse(r.Parameters), r.Status, r.Result is null ? null : Json.Parse(r.Result),
        r.Exported);
}

/// <summary>Command PrepareReturn for a return code and an accounting period (yyyy-MM, the period the return ends with).</summary>
public sealed record PrepareReturn(string ReturnCode, string PeriodCode) : ICommand<ReturnView>;

public enum ReturnAction
{
    Review,
    Approve,
    Submit,
    Accept,
    Reject,
    Correct,
}

/// <summary>Commands ReviewReturn, ApproveReturn, SubmitReturn, the regulator's answer and the correction of a rejected return.</summary>
public sealed record ChangeReturn(Guid ReturnId, ReturnAction Action, string? Reason) : ICommand<ReturnView>;

public sealed record GetReturn(Guid ReturnId) : IQuery<ReturnView>;

public sealed record ListReturns(string? PeriodCode, ReturnStatus? Status) : IQuery<IReadOnlyList<ReturnView>>;

/// <summary>A report run: the totals of the metrics named, over the dates given (OQ-064 item 4); exported only where the catalogue allows.</summary>
public sealed record RunReport(string ReportCode, DateOnly From, DateOnly To, IReadOnlyList<string> Metrics, bool Export) : ICommand<ReportRunView>;

public sealed record GetReportRun(Guid RunId) : IQuery<ReportRunView>;

public sealed class PrepareReturnValidator : AbstractValidator<PrepareReturn>
{
    public PrepareReturnValidator()
    {
        RuleFor(c => c.ReturnCode).NotEmpty().MaximumLength(20);
        RuleFor(c => c.PeriodCode).Must(p => ReturnFrequencies.Span(ReturnFrequencies.Monthly, p) is not null).WithMessage("The period is a month, yyyy-MM.");
    }
}

public sealed class RunReportValidator : AbstractValidator<RunReport>
{
    public RunReportValidator()
    {
        RuleFor(c => c.ReportCode).NotEmpty().MaximumLength(20);
        RuleFor(c => c.Metrics).NotEmpty();
        RuleFor(c => c.To).GreaterThanOrEqualTo(c => c.From);
    }
}

internal static class Json
{
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

internal static class Users
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");
}

/// <summary>
/// A return's values from its definition's field mapping (OQ-064 item 1): {"field": "account:&lt;code&gt;"} is the account's
/// movement in the period (Finance's ledger); {"field": "metric:&lt;code&gt;"} is the metric's total over the days the
/// return covers (Analytics' projections).
/// </summary>
public sealed class ReturnValues(ILedgerBalances ledger, IProjectionTotals projections, ITenantContext tenant)
{
    public async Task<Result<string>> Calculate(ReturnDefinition definition, string periodCode, CancellationToken cancellationToken)
    {
        if (definition.FieldMapping.ValueKind != JsonValueKind.Object || ReturnFrequencies.Span(definition.Frequency, periodCode) is not { } span)
        {
            return Error.Validation(ComplianceErrorCodes.MappingInvalid, $"Return {definition.ReturnCode} has no field mapping object, or {periodCode} is not a period.");
        }

        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var field in definition.FieldMapping.EnumerateObject())
        {
            var source = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()! : "";
            var colon = source.IndexOf(':', StringComparison.Ordinal);
            var (kind, code) = colon > 0 ? (source[..colon], source[(colon + 1)..]) : ("", "");
            decimal? value = kind switch
            {
                "account" => await ledger.PeriodMovement(tenant.RequireTenantId(), code, periodCode, cancellationToken),
                "metric" => await projections.Total(tenant.RequireTenantId(), code, span.From, span.To, cancellationToken),
                _ => null,
            };
            if (value is null)
            {
                return Error.Validation(ComplianceErrorCodes.MappingInvalid,
                    $"Return {definition.ReturnCode} field {field.Name} maps to {source}, which is neither an account nor a metric in force.");
            }

            values[field.Name] = value.Value;
        }

        return JsonSerializer.Serialize(values, JsonSerializerOptions.Web);
    }
}

internal sealed class PrepareReturnHandler(IComplianceRepository repository, IComplianceCodes codes, ReturnValues values, IExecutionContext execution, IClock clock,
    IAuditTrail audit) : ICommandHandler<PrepareReturn, ReturnView>
{
    public async Task<Result<ReturnView>> Handle(PrepareReturn command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var prepared = await Returns.Prepare(repository, codes, values, command.ReturnCode, command.PeriodCode, user.Value, clock.UtcToday, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error;
        }

        audit.Record(new AuditEntry("COMPLIANCE", "PrepareReturn", nameof(RegulatoryReturn), prepared.Value.Id.ToString()));
        return ReturnView.From(prepared.Value);
    }
}

internal static class Returns
{
    public static async Task<Result<RegulatoryReturn>> Prepare(IComplianceRepository repository, IComplianceCodes codes, ReturnValues values, string returnCode,
        string periodCode, Guid? preparedBy, DateOnly day, CancellationToken cancellationToken)
    {
        if (await codes.Definition(returnCode, day, cancellationToken) is not { } definition)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"{returnCode} is not a regulatory return definition in force.");
        }

        if (await repository.Return(returnCode, periodCode, cancellationToken) is not null)
        {
            return Error.Conflict(ComplianceErrorCodes.ReturnExists, $"Return {returnCode} for {periodCode} is prepared already.");
        }

        var calculated = await values.Calculate(definition, periodCode, cancellationToken);
        if (calculated.IsFailure)
        {
            return calculated.Error;
        }

        var prepared = RegulatoryReturn.PrepareReturn(Guid.CreateVersion7(), returnCode, periodCode, calculated.Value, preparedBy);
        repository.Add(prepared);
        return prepared;
    }
}

/// <summary>
/// The return's lifecycle. Approving needs the approve permission (a compliance officer); a submission goes through the
/// regulator adapter and is refused if the regulator does not accept it for processing.
/// </summary>
internal sealed class ChangeReturnHandler(IComplianceRepository repository, IComplianceCodes codes, ReturnValues values, IRegulatorAdapter regulators,
    ITenantContext tenant, IExecutionContext execution, IClock clock, IAuditTrail audit) : ICommandHandler<ChangeReturn, ReturnView>
{
    public async Task<Result<ReturnView>> Handle(ChangeReturn command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await repository.Return(command.ReturnId, cancellationToken) is not { } regulatoryReturn)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The regulatory return was not found.");
        }

        if (command.Action == ReturnAction.Approve && !execution.HasPermission(Contracts.CompliancePermissions.ReturnApprove))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "A return is approved by a compliance officer.");
        }

        audit.Record(new AuditEntry("COMPLIANCE", $"{command.Action}Return", nameof(RegulatoryReturn), regulatoryReturn.Id.ToString(),
            command.Reason is null ? null : new Dictionary<string, string?> { ["reason"] = command.Reason }));
        var result = command.Action switch
        {
            ReturnAction.Review => regulatoryReturn.ReviewReturn(user.Value),
            ReturnAction.Approve => regulatoryReturn.ApproveReturn(user.Value),
            ReturnAction.Submit => await Submit(regulatoryReturn, cancellationToken),
            ReturnAction.Accept => regulatoryReturn.RecordResponse(true, null),
            ReturnAction.Reject => regulatoryReturn.RecordResponse(false, command.Reason),
            ReturnAction.Correct => await Correct(regulatoryReturn, user.Value, cancellationToken),
            _ => Result.Failure(Error.Validation(ErrorCodes.ValidationFailed, "Unknown action.")),
        };
        return result.IsFailure ? result.Error : ReturnView.From(regulatoryReturn);
    }

    private async Task<Result> Submit(RegulatoryReturn regulatoryReturn, CancellationToken cancellationToken)
    {
        if (regulatoryReturn.Status != ReturnStatus.Approved)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"A return is submitted once approved; this one is {regulatoryReturn.Status}.");
        }

        if (await codes.Definition(regulatoryReturn.ReturnCode, clock.UtcToday, cancellationToken) is not { } definition)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"{regulatoryReturn.ReturnCode} is not a regulatory return definition in force.");
        }

        var sent = await regulators.SubmitReturn(tenant.RequireTenantId(), definition.Regulator, regulatoryReturn.ReturnCode, regulatoryReturn.PeriodCode,
            Json.Parse(regulatoryReturn.FieldValues), cancellationToken);
        return sent.Accepted
            ? regulatoryReturn.SubmitReturn(definition.Regulator, sent.PartnerRef, clock.UtcNow)
            : Error.Conflict(ErrorCodes.InvalidStateTransition, $"The regulator did not take the return: {sent.Error}");
    }

    private async Task<Result> Correct(RegulatoryReturn regulatoryReturn, Guid by, CancellationToken cancellationToken)
    {
        if (await codes.Definition(regulatoryReturn.ReturnCode, clock.UtcToday, cancellationToken) is not { } definition)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"{regulatoryReturn.ReturnCode} is not a regulatory return definition in force.");
        }

        var calculated = await values.Calculate(definition, regulatoryReturn.PeriodCode, cancellationToken);
        return calculated.IsFailure ? calculated.Error : regulatoryReturn.Correct(calculated.Value, by);
    }
}

/// <summary>A report is run by users holding one of its catalogue roles; exported only where the catalogue allows (OQ-064 item 4).</summary>
internal sealed class RunReportHandler(IComplianceRepository repository, IComplianceCodes codes, IProjectionTotals projections, IUserRoleQuery roles, ITenantContext tenant,
    IExecutionContext execution, IClock clock, IAuditTrail audit) : ICommandHandler<RunReport, ReportRunView>
{
    public async Task<Result<ReportRunView>> Handle(RunReport command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await codes.Report(command.ReportCode, clock.UtcToday, cancellationToken) is not { } report)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"{command.ReportCode} is not a report in the catalogue.");
        }

        var held = await roles.EffectiveRoles(tenant.RequireTenantId(), user.Value, cancellationToken);
        if (!report.Roles.Any(held.Contains) || (command.Export && !report.ExportAllowed))
        {
            return Error.Forbidden(ComplianceErrorCodes.ReportNotAllowed,
                command.Export && report.ExportAllowed is false ? $"Report {report.ReportCode} may not be exported." : $"Report {report.ReportCode} is not run by your roles.");
        }

        var totals = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        foreach (var metric in command.Metrics.Distinct(StringComparer.Ordinal))
        {
            totals[metric] = await projections.Total(tenant.RequireTenantId(), metric, command.From, command.To, cancellationToken);
        }

        var parameters = JsonSerializer.Serialize(new
        {
            from = command.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to = command.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), metrics = command.Metrics,
        }, JsonSerializerOptions.Web);
        var run = ReportRun.Completed(Guid.CreateVersion7(), report.ReportCode, user.Value, parameters, JsonSerializer.Serialize(totals, JsonSerializerOptions.Web),
            command.Export);
        repository.Add(run);
        audit.Record(new AuditEntry("COMPLIANCE", command.Export ? "ExportReport" : "RunReport", nameof(ReportRun), run.Id.ToString(),
            new Dictionary<string, string?> { ["report"] = report.ReportCode }));
        return ReportRunView.From(run);
    }
}

internal sealed class ComplianceQueries(IComplianceRepository repository) :
    IQueryHandler<GetReturn, ReturnView>,
    IQueryHandler<ListReturns, IReadOnlyList<ReturnView>>,
    IQueryHandler<GetReportRun, ReportRunView>
{
    public async Task<Result<ReturnView>> Handle(GetReturn query, CancellationToken cancellationToken) =>
        await repository.Return(query.ReturnId, cancellationToken) is { } r ? ReturnView.From(r) : Error.NotFound(ErrorCodes.NotFound, "The regulatory return was not found.");

    public async Task<Result<IReadOnlyList<ReturnView>>> Handle(ListReturns query, CancellationToken cancellationToken) =>
        (await repository.Returns(query.PeriodCode, query.Status, cancellationToken)).Select(ReturnView.From).ToList();

    public async Task<Result<ReportRunView>> Handle(GetReportRun query, CancellationToken cancellationToken) =>
        await repository.Run(query.RunId, cancellationToken) is { } r ? ReportRunView.From(r) : Error.NotFound(ErrorCodes.NotFound, "The report run was not found.");
}

/// <summary>
/// AccountingPeriodClosed (final) → PrepareReturn for each return definition whose frequency ends with the period
/// (OQ-064 item 2). A return that cannot be calculated (a mapping to an unknown account or metric) is retried, then
/// dead-lettered for a person.
/// </summary>
public sealed class ReturnPolicies(IComplianceRepository repository, IComplianceCodes codes, ReturnValues values, IClock clock, ILogger<ReturnPolicies> logger)
    : IIntegrationEventHandler<AccountingPeriodClosed>
{
    public async Task Handle(AccountingPeriodClosed closed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!closed.Final)
        {
            return;
        }

        foreach (var definition in await codes.Definitions(clock.UtcToday, cancellationToken))
        {
            if (!ReturnFrequencies.EndsWith(definition.Frequency, closed.PeriodCode) || await repository.Return(definition.ReturnCode, closed.PeriodCode, cancellationToken) is not null)
            {
                continue;
            }

            var prepared = await Returns.Prepare(repository, codes, values, definition.ReturnCode, closed.PeriodCode, null, clock.UtcToday, cancellationToken);
            if (prepared.IsFailure)
            {
                throw new InvalidOperationException($"Return {definition.ReturnCode} for {closed.PeriodCode} was not prepared: {prepared.Error.Message}");
            }

            logger.LogInformation("Return {ReturnCode} prepared for {PeriodCode}", definition.ReturnCode, closed.PeriodCode);
        }
    }
}
