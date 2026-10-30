/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tenancy
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
using Microsoft.Extensions.Logging;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Tenancy.Domain.Tenants;

namespace Insys.Tenancy.Application.Tenants;

// Onboarding readiness (docs/specs/addenda/onboarding-readiness.md, TEN-008): completing a step runs its checks in the
// tenant on its business date, each answered by the module that owns the configuration. A step with any check not
// ready is refused, listing them all; the readiness view lists every step's checks without completing anything.

/// <summary>The tenant's base currency and default language from its locale defaults in force, as far as they are set.</summary>
public interface ITenantLocale
{
    Task<(string? BaseCurrency, string? DefaultLanguage)> For(Guid tenantId, DateOnly day, CancellationToken cancellationToken);
}

/// <param name="RunDate">For the end-to-end run: its date, outcome (PASSED or FAILED), who ran it, and who approved the record.</param>
public sealed record OnboardingAttestationView(string Kind, string Note, Guid By, DateTimeOffset At, DateOnly? RunDate = null, string? Outcome = null, string? RanBy = null,
    Guid? ApprovedBy = null, DateTimeOffset? ApprovedAt = null);

/// <summary>Command RecordSmokeRun: the evidence of the end-to-end run, by a platform operator.</summary>
public sealed record RecordSmokeRun(Guid TenantId, string RunReference, DateOnly RunDate, string Outcome, string RanBy, long ExpectedVersion)
    : ICommand<long>, ITargetTenantRequest, IVersionedCommand
{
    public Guid TargetTenantId => TenantId;
}

public sealed class RecordSmokeRunValidator : AbstractValidator<RecordSmokeRun>
{
    public RecordSmokeRunValidator()
    {
        RuleFor(c => c.RunReference).NotEmpty().MaximumLength(500);
        RuleFor(c => c.RanBy).NotEmpty().MaximumLength(120);
        RuleFor(c => c.Outcome).Must(o => o is "PASSED" or "FAILED").WithMessage("outcome is PASSED or FAILED.");
    }
}

/// <summary>Command ApproveSmokeRun: a second platform operator approves the recorded run (SRS-002).</summary>
public sealed record ApproveSmokeRun(Guid TenantId, long ExpectedVersion) : ICommand<long>, ITargetTenantRequest, IVersionedCommand
{
    public Guid TargetTenantId => TenantId;
}

internal sealed class SmokeRunHandlers(ITenantRepository tenants, IExecutionContext execution, IClock clock, BuildingBlocks.Application.Auditing.IAuditTrail audit) :
    ICommandHandler<RecordSmokeRun, long>,
    ICommandHandler<ApproveSmokeRun, long>
{
    public Task<Result<long>> Handle(RecordSmokeRun command, CancellationToken cancellationToken) =>
        Change(command.TenantId, command.ExpectedVersion, "RecordSmokeRun", (tenant, by) =>
            tenant.RecordSmokeRun(command.RunReference, command.RunDate, command.Outcome, command.RanBy, by, clock.UtcNow), cancellationToken);

    public Task<Result<long>> Handle(ApproveSmokeRun command, CancellationToken cancellationToken) =>
        Change(command.TenantId, command.ExpectedVersion, "ApproveSmokeRun", (tenant, by) => tenant.ApproveSmokeRun(by, clock.UtcNow), cancellationToken);

    private async Task<Result<long>> Change(Guid tenantId, long expectedVersion, string action, Func<Tenant, Guid, Result> change, CancellationToken cancellationToken)
    {
        var operatorId = Operator.Require(execution);
        if (operatorId.IsFailure)
        {
            return operatorId.Error;
        }

        var tenant = await Operator.Load(tenants, tenantId, expectedVersion, cancellationToken);
        if (tenant.IsFailure)
        {
            return tenant.Error;
        }

        var result = change(tenant.Value, operatorId.Value);
        if (result.IsFailure)
        {
            return result.Error;
        }

        audit.Record(new BuildingBlocks.Application.Auditing.AuditEntry("TENANT", action, nameof(Tenant), tenantId.ToString()));
        return tenant.Value.RowVersion + 1;
    }
}

/// <param name="Attestation">The waiver or evidence that stands in for this check, if recorded.</param>
/// <param name="Advisory">A warning: shown when not ready, but it does not stop the step being completed (BR-113).</param>
public sealed record ReadinessCheckView(string Code, bool Ready, string What, IReadOnlyList<string> Missing, OnboardingAttestationView? Attestation,
    [property: BuildingBlocks.Contracts.AlwaysPresent] bool Advisory = false);

/// <param name="Label">The step's English label (BR-020), for example "Organisation and access".</param>
/// <param name="Ready">Every check that is not advisory is ready.</param>
public sealed record StepReadinessView(string Step, string Label, bool Completed, bool Ready, IReadOnlyList<ReadinessCheckView> Checks);

/// <param name="Day">The tenant's business date the checks ran on (ADR-025).</param>
public sealed record TenantReadinessView(Guid TenantId, DateOnly Day, IReadOnlyList<StepReadinessView> Steps);

public sealed record GetTenantReadiness(Guid TenantId) : IQuery<TenantReadinessView>, ITargetTenantRequest
{
    public Guid TargetTenantId => TenantId;
}

/// <summary>BR-012: the signed-in user's own tenant's readiness, so its staff see what onboarding still needs.</summary>
public sealed record GetOwnTenantReadiness : IQuery<TenantReadinessView>;

/// <summary>A waiver (TREATIES, WEBHOOKS_OR_NONE) with its reason, or evidence (SMOKE_RUN) with its reference.</summary>
public sealed record OnboardingAttestationRequest(string Check, string Note);

public sealed class OnboardingReadiness(IEnumerable<IReadinessCheck> checks, ITenantLocale locale, TenantBusinessDates dates,
    ILogger<OnboardingReadiness> logger)
{
    /// <summary>A check slower than this is logged, with its name: readiness runs inside the step's transaction.</summary>
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(2);
    public const string SmokeRun = "SMOKE_RUN";

    /// <param name="step">Only this step's checks are run (completing a step); null runs every step's (the readiness view).</param>
    public async Task<TenantReadinessView> For(Tenant tenant, CancellationToken cancellationToken, string? step = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var day = dates.TodayFor(tenant.Id);
        var (currency, language) = await locale.For(tenant.Id, day, cancellationToken);
        Warn("the business date and locale", timer);
        var context = new ReadinessContext(tenant.Id, day, currency, language, step);
        var results = new List<ReadinessResult>();
        foreach (var check in checks.Where(c => step is null || c.Steps.Contains(step)))
        {
            timer.Restart();
            results.AddRange(await check.Check(context, cancellationToken));
            Warn(check.GetType().Name, timer);
        }

        // Decided 24 October 2026: the run is made in a non-production environment with the same configuration, and recorded
        // here with its reference, date, outcome and who ran it, approved by a second person (until configuration promotion exists).
        if (step is null or "END_TO_END_TESTS")
        {
            results.Add(ReadinessResult.Of("END_TO_END_TESTS", SmokeRun,
                "A passed end-to-end run (quote, policy, invoice, collection, journal, claim registration) in a non-production environment with the same configuration, recorded and approved by a second person",
                ["the run's reference, date, outcome and who ran it (POST …/smoke-run), approved by a second person"]));
        }

        void Warn(string what, System.Diagnostics.Stopwatch elapsed)
        {
            if (elapsed.Elapsed > Slow)
            {
                logger.LogWarning("Onboarding readiness for {TenantId}: {What} took {Seconds:F1} s", tenant.Id, what, elapsed.Elapsed.TotalSeconds);
            }
        }

        var attestations = tenant.Onboarding?.Attestations ?? [];
        var current = tenant.Onboarding is { IsCompleted: false } onboarding ? OnboardingSteps.All.ToList().IndexOf(onboarding.CurrentStep) : OnboardingSteps.All.Count;
        var steps = OnboardingSteps.All.Select((step, index) =>
        {
            var views = results.Where(r => r.Step == step).GroupBy(r => r.Code).Select(g =>
            {
                var missing = g.SelectMany(r => r.Missing).Distinct(StringComparer.Ordinal).ToList();
                var attested = attestations.FirstOrDefault(a => a.Check == g.Key && a.StandsIn);
                var pending = attestations.FirstOrDefault(a => a.Check == g.Key && !a.StandsIn);
                var shown = attested ?? pending;
                return new ReadinessCheckView(g.Key, missing.Count == 0 || attested is not null, g.First().What, attested is null ? missing : [],
                    shown is null ? null : new OnboardingAttestationView(shown.Kind, shown.Note, shown.By, shown.At, shown.RunDate, shown.Outcome, shown.RanBy,
                        shown.ApprovedBy, shown.ApprovedAt), g.All(r => r.Advisory));
            }).OrderBy(v => v.Code, StringComparer.Ordinal).ToList();
            return new StepReadinessView(step, OnboardingSteps.Labels[step], index < current, views.All(v => v.Ready || v.Advisory), views);
        }).ToList();
        return new TenantReadinessView(tenant.Id, day, steps);
    }

    /// <summary>The step's checks that are not ready, as the 422's details: each check code with what is missing.</summary>
    public static Error? NotReady(TenantReadinessView readiness, string step) =>
        readiness.Steps.Single(s => s.Step == step) is { Ready: false } view
            ? Error.Validation(TenancyErrorCodes.OnboardingStepNotReady,
                $"{step} is not ready: {string.Join(", ", view.Checks.Where(c => !c.Ready && !c.Advisory).Select(c => c.Code))}.",
                view.Checks.Where(c => !c.Ready && !c.Advisory).ToDictionary(c => c.Code, c => c.Missing.ToArray()))
            : null;
}

internal sealed class GetOwnTenantReadinessHandler(ITenantRepository tenants, OnboardingReadiness readiness, ITenantContext tenant)
    : IQueryHandler<GetOwnTenantReadiness, TenantReadinessView>
{
    public async Task<Result<TenantReadinessView>> Handle(GetOwnTenantReadiness query, CancellationToken cancellationToken) =>
        await tenants.Get(tenant.RequireTenantId(), cancellationToken) is { } own
            ? await readiness.For(own, cancellationToken)
            : Error.NotFound(ErrorCodes.NotFound, "The tenant was not found.");
}

internal sealed class GetTenantReadinessHandler(ITenantRepository tenants, OnboardingReadiness readiness) : IQueryHandler<GetTenantReadiness, TenantReadinessView>
{
    public async Task<Result<TenantReadinessView>> Handle(GetTenantReadiness query, CancellationToken cancellationToken) =>
        await tenants.Get(query.TenantId, cancellationToken) is { } tenant
            ? await readiness.For(tenant, cancellationToken)
            : Error.NotFound(ErrorCodes.NotFound, "The tenant was not found.");
}
