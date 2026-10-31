/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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
using FluentValidation;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;

namespace Insys.Integration.Application.Parking;

// ADR-022: deliveries parked for missing tenant configuration, waiting behind them, or failing. The ledger itself is
// written by the event bus; this is the operations view and its manual actions.

public sealed record ParkedDeliveryView(
    Guid Id, string Consumer, Guid EventId, string EventType, DateTimeOffset OccurredAt, string AggregateType, Guid AggregateId, LedgerStatus Status,
    string? PrerequisiteCode, string? PrerequisiteItem, DateTimeOffset? ParkedAt, int Attempts, DateTimeOffset? LastAttemptAt, int EscalationLevel,
    DateTimeOffset? EscalatedAt, DateTimeOffset? NextEscalationAt, DateTimeOffset? ResolvedAt, string? DiscardedBy, string? DiscardReason, long RowVersion);

/// <summary>One kind of parked delivery in one tenant: the platform operator's view (ADR-022 decision 5).</summary>
public sealed record ParkedDeliveryCount(Guid TenantId, string? TenantCode, string Consumer, string Code, string? Item, int Count, int EscalationLevel,
    DateTimeOffset OldestParkedAt);

/// <summary>A tenant's escalation of parked deliveries (ADR-022 decision 6). Durations are ISO 8601 in business time.</summary>
public sealed record ParkingSettingView(string ConfigurationOwnerRole, string? CalendarCode, string MoneyFirst, string MoneySecond, string OtherFirst,
    string OtherSecond, bool IsDefault, long RowVersion);

public sealed record ParkedDeliveryFilter(LedgerStatus? Status, string? Code, string? Consumer, string? AggregateType, Guid? AggregateId);

public interface IParkedDeliveryStore
{
    Task<IReadOnlyList<ParkedDeliveryView>> List(ParkedDeliveryFilter filter, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<ParkedDeliveryView?> Get(Guid id, CancellationToken cancellationToken);

    /// <summary>Counts of parked deliveries per active tenant and kind, read tenant by tenant.</summary>
    Task<IReadOnlyList<ParkedDeliveryCount>> ByTenant(CancellationToken cancellationToken);

    /// <summary>The Integration dashboard figures (GAP-046) for the tenant.</summary>
    Task<DashboardSummary> Summary(CancellationToken cancellationToken);

    /// <summary>Asks for a retry at the next pass of the parking job. Returns how many deliveries it covers.</summary>
    Task<int> RequestRetry(Guid? id, string? code, CancellationToken cancellationToken);

    /// <summary>Closes an open delivery without delivering it. False when it is not open or has changed.</summary>
    Task<bool> Discard(Guid id, long expectedVersion, string by, string reason, CancellationToken cancellationToken);

    Task<ParkingSettingView> Setting(CancellationToken cancellationToken);

    Task<long> SaveSetting(ParkingSettingView setting, long? expectedVersion, string by, CancellationToken cancellationToken);
}

public sealed record ListParkedDeliveries(ParkedDeliveryFilter Filter, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ParkedDeliveryView>>;

public sealed record GetParkedDelivery(Guid Id) : IQuery<ParkedDeliveryView>;

public sealed record ListParkedDeliveriesByTenant : IQuery<IReadOnlyList<ParkedDeliveryCount>>;

/// <summary>Retry one parked delivery, or every parked delivery of the tenant for one code (ADR-022 decision 4).</summary>
public sealed record RetryParkedDeliveries(Guid? Id, string? Code) : ICommand<int>;

/// <summary>Discard a delivery: never automatic, always with a reason, and audited (ADR-022 decision 6).</summary>
public sealed record DiscardParkedDelivery(Guid Id, long ExpectedVersion, string Reason) : ICommand<long>, IVersionedCommand;

public sealed record GetParkingSetting : IQuery<ParkingSettingView>;

public sealed record ChangeParkingSetting(string ConfigurationOwnerRole, string? CalendarCode, string MoneyFirst, string MoneySecond, string OtherFirst,
    string OtherSecond, long? ExpectedVersion) : ICommand<long>;

/// <summary>
/// The Integration dashboard figures (GAP-046, addenda/dashboard-figures.md): DELIVERIES_PARKED by prerequisite code;
/// DELIVERIES_FAILING (failing, retrying or waiting behind one); DEAD_LETTERS_OPEN (no resolution yet);
/// WEBHOOK_DELIVERIES_FAILING (failed for good, or pending after a failed attempt); OLDEST_PARKED_AT; JOB_FUNCTIONS_OFF (the JOB
/// functions the tenant switched off, by code, whose work waits: OQ-098).
/// </summary>
public sealed record DeliveriesSummaryQuery : IQuery<DashboardSummary>;

public static class DeliveryFigures
{
    public const string Parked = "DELIVERIES_PARKED";
    public const string Failing = "DELIVERIES_FAILING";
    public const string DeadLettersOpen = "DEAD_LETTERS_OPEN";
    public const string WebhooksFailing = "WEBHOOK_DELIVERIES_FAILING";
    public const string OldestParkedAt = "OLDEST_PARKED_AT";
    public const string JobFunctionsOff = "JOB_FUNCTIONS_OFF";
}

internal sealed class DeliveriesSummaryHandler(IParkedDeliveryStore store, IFunctionAvailability availability, ITenantContext tenant)
    : IQueryHandler<DeliveriesSummaryQuery, DashboardSummary>
{
    public async Task<Result<DashboardSummary>> Handle(DeliveriesSummaryQuery query, CancellationToken cancellationToken)
    {
        var summary = await store.Summary(cancellationToken);
        var off = await availability.JobsSwitchedOff(tenant.RequireTenantId(), cancellationToken);
        return summary with
        {
            Figures = [.. summary.Figures, new DashboardFigure(DeliveryFigures.JobFunctionsOff, off.Count, By: off.Select(code => new FigurePart(code, 1)).ToList())],
        };
    }
}

public static class ParkingErrorCodes
{
    [ErrorCode(409, "error.integration.parked_delivery_not_open")]
    public const string NotOpen = "PARKED_DELIVERY_NOT_OPEN";
}

public sealed class ListParkedDeliveriesValidator : AbstractValidator<ListParkedDeliveries>
{
    public ListParkedDeliveriesValidator()
    {
        RuleFor(q => q.Limit).InclusiveBetween(1, 200);
        RuleFor(q => q.Filter.Code).MaximumLength(60);
        RuleFor(q => q.Filter.Consumer).MaximumLength(60);
        RuleFor(q => q.Filter.AggregateType).MaximumLength(60);
    }
}

public sealed class RetryParkedDeliveriesValidator : AbstractValidator<RetryParkedDeliveries>
{
    public RetryParkedDeliveriesValidator()
    {
        RuleFor(c => c).Must(c => (c.Id is null) != string.IsNullOrWhiteSpace(c.Code)).WithMessage("Give either a parked delivery or a code.");
        RuleFor(c => c.Code).MaximumLength(60);
    }
}

public sealed class DiscardParkedDeliveryValidator : AbstractValidator<DiscardParkedDelivery>
{
    public DiscardParkedDeliveryValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(200);
}

public sealed class ChangeParkingSettingValidator : AbstractValidator<ChangeParkingSetting>
{
    public ChangeParkingSettingValidator()
    {
        RuleFor(c => c.ConfigurationOwnerRole).NotEmpty().MaximumLength(40);
        RuleFor(c => c.CalendarCode).MaximumLength(30);
        foreach (var duration in new System.Linq.Expressions.Expression<Func<ChangeParkingSetting, string>>[] { c => c.MoneyFirst, c => c.MoneySecond, c => c.OtherFirst, c => c.OtherSecond })
        {
            RuleFor(duration).NotEmpty().MaximumLength(20).Must(BusinessDuration.IsValid).WithMessage("A duration is ISO 8601, such as PT4H or P1D.");
        }
    }
}

/// <summary>ISO 8601 durations in business time: whole days are working days (ADR-022 decision 6).</summary>
public static class BusinessDuration
{
    public static bool IsValid(string? value) => value is not null && TryParse(value, out var span) && span > TimeSpan.Zero;

    public static bool TryParse(string value, out TimeSpan span)
    {
        try
        {
            span = System.Xml.XmlConvert.ToTimeSpan(value);
            return true;
        }
        catch (FormatException)
        {
            span = TimeSpan.Zero;
            return false;
        }
    }
}

internal sealed class ParkedDeliveryHandlers(IParkedDeliveryStore store, IExecutionContext execution, IAuditTrail audit)
    : IQueryHandler<ListParkedDeliveries, IReadOnlyList<ParkedDeliveryView>>,
      IQueryHandler<GetParkedDelivery, ParkedDeliveryView>,
      IQueryHandler<ListParkedDeliveriesByTenant, IReadOnlyList<ParkedDeliveryCount>>,
      IQueryHandler<GetParkingSetting, ParkingSettingView>,
      ICommandHandler<RetryParkedDeliveries, int>,
      ICommandHandler<DiscardParkedDelivery, long>,
      ICommandHandler<ChangeParkingSetting, long>
{
    public async Task<Result<IReadOnlyList<ParkedDeliveryView>>> Handle(ListParkedDeliveries query, CancellationToken cancellationToken) =>
        Result.Success(await store.List(query.Filter, query.AfterId, query.Limit, cancellationToken));

    public async Task<Result<ParkedDeliveryView>> Handle(GetParkedDelivery query, CancellationToken cancellationToken) =>
        await store.Get(query.Id, cancellationToken) is { } view ? view : Error.NotFound(ErrorCodes.NotFound, "The parked delivery was not found.");

    public async Task<Result<IReadOnlyList<ParkedDeliveryCount>>> Handle(ListParkedDeliveriesByTenant query, CancellationToken cancellationToken) =>
        Result.Success(await store.ByTenant(cancellationToken));

    public async Task<Result<ParkingSettingView>> Handle(GetParkingSetting query, CancellationToken cancellationToken) =>
        await store.Setting(cancellationToken);

    public async Task<Result<int>> Handle(RetryParkedDeliveries command, CancellationToken cancellationToken)
    {
        if (command.Id is { } id && await store.Get(id, cancellationToken) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The parked delivery was not found.");
        }

        var count = await store.RequestRetry(command.Id, command.Code, cancellationToken);
        audit.Record(new AuditEntry("INTEGRATION", "RetryParkedDeliveries", "ParkedDelivery", command.Id?.ToString() ?? command.Code!,
            new Dictionary<string, string?> { ["code"] = command.Code, ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        return count;
    }

    public async Task<Result<long>> Handle(DiscardParkedDelivery command, CancellationToken cancellationToken)
    {
        if (await store.Get(command.Id, cancellationToken) is not { } delivery)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The parked delivery was not found.");
        }

        if (delivery.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The parked delivery has changed since it was read.");
        }

        if (!await store.Discard(command.Id, command.ExpectedVersion, execution.ActorId, command.Reason, cancellationToken))
        {
            return Error.Conflict(ParkingErrorCodes.NotOpen, "Only a parked, waiting, failing or dead-lettered delivery can be discarded.");
        }

        audit.Record(new AuditEntry("INTEGRATION", "DiscardParkedDelivery", "ParkedDelivery", command.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["consumer"] = delivery.Consumer,
                ["eventType"] = delivery.EventType,
                ["code"] = delivery.PrerequisiteCode,
                ["reason"] = command.Reason,
            }));
        return delivery.RowVersion;
    }

    public async Task<Result<long>> Handle(ChangeParkingSetting command, CancellationToken cancellationToken)
    {
        var current = await store.Setting(cancellationToken);
        if (command.ExpectedVersion is { } expected ? expected != current.RowVersion : !current.IsDefault)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The parking setting has changed since it was read.");
        }

        var setting = new ParkingSettingView(command.ConfigurationOwnerRole, command.CalendarCode, command.MoneyFirst, command.MoneySecond, command.OtherFirst,
            command.OtherSecond, false, current.RowVersion);
        var version = await store.SaveSetting(setting, current.IsDefault ? null : current.RowVersion, execution.ActorId, cancellationToken);
        audit.Record(new AuditEntry("INTEGRATION", "ChangeParkingSetting", "ParkingSetting", "parking",
            new Dictionary<string, string?>
            {
                ["configurationOwnerRole"] = setting.ConfigurationOwnerRole,
                ["calendarCode"] = setting.CalendarCode,
                ["money"] = $"{setting.MoneyFirst}/{setting.MoneySecond}",
                ["other"] = $"{setting.OtherFirst}/{setting.OtherSecond}",
            }));
        return version;
    }
}
