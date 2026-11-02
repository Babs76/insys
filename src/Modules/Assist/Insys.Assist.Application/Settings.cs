/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Microsoft.Extensions.Options;

namespace Insys.Assist.Application;

public interface IAssistRepository
{
    Task<AssistTenantSetting?> Setting(CancellationToken cancellationToken);

    void Add(AssistTenantSetting setting);

    void Add(AssistRun run);

    void Add(AssistRunPayload payload);

    Task<AssistUsage> Usage(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

/// <summary>A tenant's settings. Before any are made, the tenant has none, and Assist refuses.</summary>
public sealed record AssistSettingsView(Guid Id, string? Jurisdiction, string? InferenceRegion, string? LegalBasis, bool ExplanationsEnabled, long? MonthlyTokenCap, bool ResidencyAllowsInference,
    long RowVersion)
{
    public static readonly AssistSettingsView None = new(Guid.Empty, null, null, null, false, null, false, 0);

    public static AssistSettingsView From(AssistTenantSetting s) =>
        new(s.Id, s.Jurisdiction, s.InferenceRegion, s.LegalBasis, s.ExplanationsEnabled, s.MonthlyTokenCap, s.ResidencyAllowsInference, s.RowVersion);
}

public sealed record GetAssistSettings : IQuery<AssistSettingsView>;

/// <summary>A tenant's use in a month, for the monthly usage report (addendum §6). Billing is outside INSYS.</summary>
public sealed record AssistUsageView(string Month, long Tokens, long Runs, long Rejected, long Unavailable, long MonthlyTokenCap);

/// <param name="Month">yyyy-MM, in the tenant's time zone; this month when not given.</param>
public sealed record GetAssistUsage(string? Month) : IQuery<AssistUsageView>;

/// <param name="ExpectedVersion">From If-Match; none for the first time they are made.</param>
public sealed record SetAssistSettings(string Jurisdiction, string InferenceRegion, string? LegalBasis, bool ExplanationsEnabled, long? ExpectedVersion = null)
    : ICommand<AssistSettingsView>, IRetryOnConflict;

public sealed class SetAssistSettingsValidator : AbstractValidator<SetAssistSettings>
{
    public SetAssistSettingsValidator()
    {
        RuleFor(c => c.Jurisdiction).NotEmpty().Matches("^[A-Za-z]{2}$");
        RuleFor(c => c.InferenceRegion).NotEmpty().MaximumLength(20);
        RuleFor(c => c.LegalBasis).MaximumLength(500);
    }
}

internal sealed class AssistSettingsHandlers(IAssistRepository assist, IBusinessDate businessDate, IOptions<AssistOptions> options)
    : IQueryHandler<GetAssistSettings, AssistSettingsView>, IQueryHandler<GetAssistUsage, AssistUsageView>, ICommandHandler<SetAssistSettings, AssistSettingsView>
{
    public async Task<Result<AssistUsageView>> Handle(GetAssistUsage query, CancellationToken cancellationToken)
    {
        var today = businessDate.Today;
        var first = new DateOnly(today.Year, today.Month, 1);
        if (query.Month is { } month && !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out first))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "month is yyyy-MM.");
        }

        var usage = await assist.Usage(businessDate.StartOf(first), businessDate.StartOf(first.AddMonths(1)), cancellationToken);
        return new AssistUsageView(first.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), usage.Tokens, usage.Runs, usage.Rejected, usage.Unavailable,
            (await assist.Setting(cancellationToken))?.MonthlyTokenCap ?? options.Value.DefaultMonthlyTokenCap);
    }

    public async Task<Result<AssistSettingsView>> Handle(GetAssistSettings query, CancellationToken cancellationToken) =>
        await assist.Setting(cancellationToken) is { } setting ? AssistSettingsView.From(setting) : AssistSettingsView.None;

    public async Task<Result<AssistSettingsView>> Handle(SetAssistSettings command, CancellationToken cancellationToken)
    {
        var current = await assist.Setting(cancellationToken);
        if (current is not null && command.ExpectedVersion is null)
        {
            return new Error(ErrorCodes.PreconditionRequired, "Changing the settings needs an If-Match header with the ETag from a GET.", ErrorKind.PreconditionRequired);
        }

        if (command.ExpectedVersion is { } expected && current?.RowVersion != expected)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The settings have changed since they were read. Read them again and retry.");
        }

        var setting = current ?? AssistTenantSetting.Start();
        setting.Change(command.Jurisdiction.ToUpperInvariant(), command.InferenceRegion.ToUpperInvariant(), command.LegalBasis, command.ExplanationsEnabled);
        if (current is null)
        {
            assist.Add(setting);
        }

        return AssistSettingsView.From(setting);
    }
}
