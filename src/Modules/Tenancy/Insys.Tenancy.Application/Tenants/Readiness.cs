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

public sealed record OnboardingAttestationView(string Kind, string Note, Guid By, DateTimeOffset At);

/// <param name="Attestation">The waiver or evidence that stands in for this check, if recorded.</param>
public sealed record ReadinessCheckView(string Code, bool Ready, string What, IReadOnlyList<string> Missing, OnboardingAttestationView? Attestation);

public sealed record StepReadinessView(string Step, bool Completed, bool Ready, IReadOnlyList<ReadinessCheckView> Checks);

/// <param name="Day">The tenant's business date the checks ran on (ADR-025).</param>
public sealed record TenantReadinessView(Guid TenantId, DateOnly Day, IReadOnlyList<StepReadinessView> Steps);

public sealed record GetTenantReadiness(Guid TenantId) : IQuery<TenantReadinessView>, ITargetTenantRequest
{
    public Guid TargetTenantId => TenantId;
}

/// <summary>A waiver (TREATIES, WEBHOOKS_OR_NONE) with its reason, or evidence (SMOKE_RUN) with its reference.</summary>
public sealed record OnboardingAttestationRequest(string Check, string Note);

public sealed class OnboardingReadiness(IEnumerable<IReadinessCheck> checks, ITenantLocale locale, TenantBusinessDates dates)
{
    public const string SmokeRun = "SMOKE_RUN";

    public async Task<TenantReadinessView> For(Tenant tenant, CancellationToken cancellationToken)
    {
        var day = dates.TodayFor(tenant.Id);
        var (currency, language) = await locale.For(tenant.Id, day, cancellationToken);
        var context = new ReadinessContext(tenant.Id, day, currency, language);
        var results = new List<ReadinessResult>();
        foreach (var check in checks)
        {
            results.AddRange(await check.Check(context, cancellationToken));
        }

        // Proposed in the addendum as a step the operator records, with its reference, until an automated run exists.
        results.Add(ReadinessResult.Of("END_TO_END_TESTS", SmokeRun, "A recorded end-to-end run: quote, policy, invoice, collection, journal and claim registration",
            ["the run's reference, recorded as evidence"]));

        var attestations = tenant.Onboarding?.Attestations ?? [];
        var current = tenant.Onboarding is { IsCompleted: false } onboarding ? OnboardingSteps.All.ToList().IndexOf(onboarding.CurrentStep) : OnboardingSteps.All.Count;
        var steps = OnboardingSteps.All.Select((step, index) =>
        {
            var views = results.Where(r => r.Step == step).GroupBy(r => r.Code).Select(g =>
            {
                var missing = g.SelectMany(r => r.Missing).Distinct(StringComparer.Ordinal).ToList();
                var attested = attestations.FirstOrDefault(a => a.Check == g.Key);
                return new ReadinessCheckView(g.Key, missing.Count == 0 || attested is not null, g.First().What, attested is null ? missing : [],
                    attested is null ? null : new OnboardingAttestationView(attested.Kind, attested.Note, attested.By, attested.At));
            }).OrderBy(v => v.Code, StringComparer.Ordinal).ToList();
            return new StepReadinessView(step, index < current, views.All(v => v.Ready), views);
        }).ToList();
        return new TenantReadinessView(tenant.Id, day, steps);
    }

    /// <summary>The step's checks that are not ready, as the 422's details: each check code with what is missing.</summary>
    public static Error? NotReady(TenantReadinessView readiness, string step) =>
        readiness.Steps.Single(s => s.Step == step) is { Ready: false } view
            ? Error.Validation(TenancyErrorCodes.OnboardingStepNotReady, $"{step} is not ready: {string.Join(", ", view.Checks.Where(c => !c.Ready).Select(c => c.Code))}.",
                view.Checks.Where(c => !c.Ready).ToDictionary(c => c.Code, c => c.Missing.ToArray()))
            : null;
}

internal sealed class GetTenantReadinessHandler(ITenantRepository tenants, OnboardingReadiness readiness) : IQueryHandler<GetTenantReadiness, TenantReadinessView>
{
    public async Task<Result<TenantReadinessView>> Handle(GetTenantReadiness query, CancellationToken cancellationToken) =>
        await tenants.Get(query.TenantId, cancellationToken) is { } tenant
            ? await readiness.For(tenant, cancellationToken)
            : Error.NotFound(ErrorCodes.NotFound, "The tenant was not found.");
}
