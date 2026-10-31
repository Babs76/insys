/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Infrastructure;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Health.Application;
using Insys.Health.Contracts;
using Insys.Health.Infrastructure.Persistence;
using Insys.Health.Infrastructure.StaticData;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.Health.Infrastructure;

public static class HealthInfrastructure
{
    public static IServiceCollection AddHealthInfrastructure(this IServiceCollection services)
    {
        // Its approval kinds and the screens that open their records (BR-005).
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.Health.Contracts.HealthApprovals.RunBusinessRefType, "HEALTH_PAYMENT_RUNS", Insys.Health.Contracts.HealthApprovals.RunApprovalType);
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.Health.Contracts.HealthApprovals.RecoveryBusinessRefType, "HEALTH_RECOVERIES", Insys.Health.Contracts.HealthApprovals.RecoveryWriteOffApprovalType, Insys.Health.Contracts.HealthApprovals.RecoveryFindingApprovalType);
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.Declare(Insys.Health.Contracts.HealthApprovals.CreditNoteBusinessRefType, "HEALTH_CREDIT_NOTES", Insys.Health.Contracts.HealthApprovals.CreditNoteApprovalType);
        BuildingBlocks.Contracts.Approvals.ApprovalKinds.RaisedWith("HEALTH", Insys.Health.Contracts.HealthApprovals.RunApprovalType, Insys.Health.Contracts.HealthApprovals.RecoveryWriteOffApprovalType,
            Insys.Health.Contracts.HealthApprovals.RecoveryFindingApprovalType, Insys.Health.Contracts.HealthApprovals.CreditNoteApprovalType);
        // ADR-026 §2: this module's event consumers run under its JOB function.
        BuildingBlocks.Application.Context.JobFunctions.ForConsumers(Persistence.HealthDbContext.SchemaName, "HEALTH_EVENT_PROCESSING");
        const string schema = HealthDbContext.SchemaName;
        services.AddModuleDbContext<HealthDbContext>(schema, "Health");
        // D-34: a legal hold on a Health membership or claim itself, placed only within the caller's scope (F103).
        services.AddLegalHoldTargets(schema, new Dictionary<string, string>
        {
            ["HEALTH_MEMBERSHIP"] = "SELECT product_code, org_node_code FROM health.membership WHERE id = @id",
            ["HEALTH_CLAIM"] = """
                SELECT m.product_code, m.org_node_code FROM health.medical_claim c JOIN health.membership m ON m.tenant_id = c.tenant_id AND m.id = c.membership_id WHERE c.id = @id
                """,
        });
        // BR-030: the memberships (a Health policy) on a product version.
        services.AddProductVersionReferences(schema, """
            SELECT 'HEALTH membership ' || member_number FROM health.membership WHERE product_code = @code AND product_version = @version ORDER BY 1
            """);
        services.AddSingleton<BuildingBlocks.Infrastructure.StaticData.IStaticRecordValidator, Persistence.HealthFraudIndicatorValidator>();
        services.AddStaticData<HealthDbContext>(schema, HealthStaticData.Descriptors);
        services.AddModuleHandlers<HealthDbContext>(typeof(EnrolHealthMember).Assembly);
        services.AddDomainEventMapper<HealthEventMapper>(schema);
        services.AddScoped<IHealthRepository, HealthRepository>();
        services.AddScoped<Workflow.Contracts.IPortalCaseLinks, Application.PortalHealthClaimCaseLinks>();
        services.AddSingleton<Contracts.IHealthNoticeRecipients, HealthNoticeRecipients>();
        services.AddScoped<IHealthPortalQueries, HealthPortalQueries>();
        services.AddSingleton<BuildingBlocks.Infrastructure.StaticData.IStaticSetGuard, PlanProductsGuard>();
        services.AddSingleton<BuildingBlocks.Contracts.IBusinessRecordScopes, HealthRemittanceScopes>();
        services.AddSingleton<Contracts.IProviderDirectory, ProviderDirectory>();
        services.AddSingleton<Workflow.Contracts.IApprovalScopeSource, PaymentRunApprovalScopes>();
        services.AddHealthChecks().AddCheck<PlanProductsHealthCheck>("health-plan-products");
        services.AddScoped<IHealthCodes, HealthCodes>();
        // F028: a warning: with no measure configured, providers' months are shown but never judged.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, Persistence.HealthDbContext.SchemaName, ["DOMAIN_REFERENCE_DATA"], async (context, connection, transaction, ct) =>
        {
            var measures = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction,
                $"SELECT count(*) FROM health.cfg_provider_measures WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day));
            var plans = await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction,
                $"SELECT count(*) FROM health.cfg_plans_and_options WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day));
            return [BuildingBlocks.Application.ReadinessResult.Warning("DOMAIN_REFERENCE_DATA", "PROVIDER_MEASURES",
                "A tenant selling Health watches its providers with measures; with none, their months are shown but not judged (a warning, not a blocker)",
                plans > 0 && measures == 0 ? ["health.cfg_provider_measures"] : [])];
        });
        services.AddScoped<HealthDeclineRights>(); // OQ-102
        services.AddScoped<IHealthLists, HealthLists>();
        services.AddScoped<BenefitContexts>();
        services.AddScoped<DependantPrivacy>(); // OQ-161
        // Wave 5b §6: a pharmacy format in force needs a formulary and prescription benefits for each plan.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, Persistence.HealthDbContext.SchemaName, ["DOMAIN_REFERENCE_DATA"], async (context, connection, transaction, ct) =>
        {
            var inForce = BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce;
            var parameters = new (string, object)[] { ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day) };
            if (await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction,
                    $"SELECT count(*) FROM health.cfg_claim_format_definitions WHERE tenant_id = @t AND line_kind = 'PHARMACY' AND {inForce}", ct, parameters) == 0)
            {
                return [];
            }

            var missing = new List<string>();
            if (await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction,
                    "SELECT count(*) FROM health.ref_medicine_formulary WHERE (tenant_id = @t OR tenant_id IS NULL) AND record_status IN ('APPROVED', 'ACTIVE') AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)", ct, parameters) == 0)
            {
                missing.Add("health.ref_medicine_formulary");
            }

            if (await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction,
                    $"""
                    SELECT count(*) FROM (SELECT DISTINCT plan_code FROM health.cfg_plans_and_options WHERE tenant_id = @t AND {inForce}) p
                    WHERE NOT EXISTS (SELECT 1 FROM health.cfg_prescription_benefits b WHERE b.tenant_id = @t AND (b.plan_code IS NULL OR b.plan_code = p.plan_code)
                      AND b.record_status = ANY(@inForce) AND b.effective_from <= @day AND (b.effective_to IS NULL OR b.effective_to > @day))
                    """, ct, parameters) > 0)
            {
                missing.Add("health.cfg_prescription_benefits");
            }

            return missing.Count == 0 ? [] : [BuildingBlocks.Application.ReadinessResult.Of("DOMAIN_REFERENCE_DATA", "PHARMACY_BENEFITS",
                "A pharmacy claim format needs a formulary and prescription benefits for each plan", missing)];
        });
        services.AddIntegrationEventHandler<PreauthorisationApproved, HealthEventPolicies, HealthDbContext>(schema, "PreauthorisationApproved");
        services.AddIntegrationEventHandler<Workflow.Contracts.CaseDecided, ReopenMedicalClaimsOnUpheldCase, HealthDbContext>(schema, "CaseDecided");
        services.AddIntegrationEventHandler<PreauthorisationReservationsChanged, HealthEventPolicies, HealthDbContext>(schema, "ReservationsChanged");
        services.AddIntegrationEventHandler<MedicalClaimAdjudicated, HealthEventPolicies, HealthDbContext>(schema, "MedicalClaimAdjudicated");
        services.AddIntegrationEventHandler<ApprovalGranted, HealthEventPolicies, HealthDbContext>(schema, "ApprovalGranted");
        // F006: grace for contributions.
        services.AddIntegrationEventHandler<Billing.Contracts.ArrearsDetected, HealthGrace, HealthDbContext>(schema, "Grace.ArrearsDetected");
        services.AddIntegrationEventHandler<Billing.Contracts.InvoiceSettled, HealthGrace, HealthDbContext>(schema, "Grace.InvoiceSettled");
        services.AddIntegrationEventHandler<ApprovalRejected, HealthEventPolicies, HealthDbContext>(schema, "ApprovalRejected");
        services.AddIntegrationEventHandler<ProviderPaymentRunReleased, HealthEventPolicies, HealthDbContext>(schema, "ProviderPaymentRunReleased");
        services.AddIntegrationEventHandler<PaymentSucceeded, HealthEventPolicies, HealthDbContext>(schema, "PaymentSucceeded");
        services.AddIntegrationEventHandler<Documents.Contracts.DocumentGenerated, HealthEventPolicies, HealthDbContext>(schema, "DocumentGenerated");
        // Wave 5a §4.6 to §4.8: recoveries, credit notes and disputes.
        services.AddScoped<RecoveryRaiser>();
        services.AddIntegrationEventHandler<MedicalClaimReversed, RecoveryPolicies, HealthDbContext>(schema, "Recovery.Reversed");
        services.AddIntegrationEventHandler<ApprovalGranted, RecoveryPolicies, HealthDbContext>(schema, "Recovery.Granted");
        services.AddIntegrationEventHandler<ApprovalRejected, RecoveryPolicies, HealthDbContext>(schema, "Recovery.Rejected");
        services.AddIntegrationEventHandler<Workflow.Contracts.CaseOpened, RecoveryPolicies, HealthDbContext>(schema, "Recovery.CaseOpened");
        services.AddIntegrationEventHandler<Workflow.Contracts.CaseClosed, RecoveryPolicies, HealthDbContext>(schema, "Recovery.CaseClosed");
        services.AddIntegrationEventHandler<Billing.Contracts.InvoiceIssued, RecoveryPolicies, HealthDbContext>(schema, "Recovery.InvoiceIssued");
        services.AddIntegrationEventHandler<Billing.Contracts.InvoiceSettled, RecoveryPolicies, HealthDbContext>(schema, "Recovery.InvoiceSettled");
        services.AddOptions<HealthJobOptions>().BindConfiguration("Health:Jobs");
        services.AddSingleton<HealthJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<HealthJobsService>();
        }

        services.AddSingleton<BuildingBlocks.Application.Context.IPartyLinkedRecords, Persistence.HealthPartyRecords>(); // D-34: the anonymisation check
        services.AddSingleton<BuildingBlocks.Application.Context.IPortalRecordRelations, HealthPortalRelations>(); // external-user access §2
        // Onboarding readiness DOMAIN_CODES: once the tenant sets HEALTH up (its numbering scheme), its own reference data is in force too.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddSqlReadiness(services, Persistence.HealthDbContext.SchemaName, ["DOMAIN_REFERENCE_DATA"], async (context, connection, transaction, ct) =>
        {
            var missing = new List<string>();
            if (await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction, $"SELECT count(*) FROM health.cfg_member_numbering_schemes WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                    ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day)) > 0)
            {
            if (await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction, $"SELECT count(*) FROM health.cfg_plans_and_options WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                    ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day)) == 0)
            {
                missing.Add("health.cfg_plans_and_options");
            }

            // V0094: eligibility checks the service against the tenant's service types.
            if (await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction, $"SELECT count(*) FROM health.cfg_service_types WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                    ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day)) == 0)
            {
                missing.Add("health.cfg_service_types");
            }

            // F006 (Wave 4): contributions in arrears need a grace rule (a scheme's own, or one naming no scheme).
            if (await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Count(connection, transaction, $"SELECT count(*) FROM health.cfg_contribution_grace_rules WHERE tenant_id = @t AND {BuildingBlocks.Infrastructure.Readiness.ReadinessSql.InForce}", ct,
                    ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day)) == 0)
            {
                missing.Add("health.cfg_contribution_grace_rules");
            }

            // Wave 4 §4.5: a scheme re-rated on experience (or blended) needs the next year's contribution table loaded by hand.
            static string InForce(string a) => $"{a}.record_status = ANY(@inForce) AND {a}.effective_from <= @day AND ({a}.effective_to IS NULL OR {a}.effective_to > @day)";
            missing.AddRange(await BuildingBlocks.Infrastructure.Readiness.ReadinessSql.Strings(connection, transaction, $"""
                SELECT 'health.cfg_contribution_rules: ' || s.scheme_number || ' from ' || to_char(s.renewal_date, 'YYYY-MM-DD') || ' (' || r.re_rating_basis || ')'
                FROM health.scheme s JOIN health.cfg_scheme_renewal_rules r ON r.tenant_id = s.tenant_id AND r.scheme = s.scheme_number AND {InForce("r")}
                WHERE s.tenant_id = @t AND s.status = 'ACTIVE' AND r.re_rating_basis IN ('EXPERIENCE', 'BLENDED')
                  AND NOT EXISTS (SELECT 1 FROM health.cfg_contribution_rules c WHERE c.tenant_id = @t AND c.scheme = s.scheme_number
                                  AND c.record_status = ANY(@inForce) AND c.effective_from = s.renewal_date)
                ORDER BY 1
                """, ct, ("t", context.TenantId), ("inForce", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Configuration.InForce), ("day", context.Day)));
            }

            return [BuildingBlocks.Application.ReadinessResult.Of("DOMAIN_REFERENCE_DATA", "DOMAIN_CODES",
                "Each domain the tenant sets up has its own reference data: Life claim and benefit types, General cancellation reasons, Health plans, service types and contribution grace", missing)];
        });
        return services;
    }
}

public sealed class HealthJobOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// The daily transitions, one record per transaction, as a service identity: approved authorisations past their
/// validity expire and release their reservation (HLT-005); memberships and schemes start.
/// </summary>
public sealed class HealthJobs(IServiceScopeFactory scopes, ITenantDirectory tenants, TenantBusinessDates businessDates, IFunctionAvailability availability, ILogger<HealthJobs> logger)
{
    /// <summary>The JOB function this job runs under (ADR-026 §2).</summary>
    public const string Function = "HEALTH_DAILY_JOBS";

    public const string ServiceName = "health-jobs";

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var done = 0;
        foreach (var tenantId in await tenants.Running(availability, Function, logger, cancellationToken))
        {
            // The tenant's business date (GAP-044, ADR-025).
            var day = businessDates.TodayFor(tenantId);
            await using (var scope = Scope(tenantId))
            {
                var assigned = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new AssignPlanProducts(day), cancellationToken);
                done += assigned.IsSuccess ? assigned.Value : 0;
            }

            HealthDueWork work;
            await using (var scope = Scope(tenantId))
            {
                var found = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new FindHealthDueWork(day, 500), cancellationToken);
                if (found.IsFailure)
                {
                    continue;
                }

                work = found.Value;
            }

            done += await ReleaseHeldClaims(tenantId, work.HeldClaimsToRelease ?? [], cancellationToken);

            foreach (var (id, action) in work.AuthorisationsToExpire.Select(id => (id, HealthDueAction.ExpireAuthorisation))
                         .Concat(work.MembershipsToActivate.Select(id => (id, HealthDueAction.ActivateMembership)))
                         .Concat(work.SchemesToActivate.Select(id => (id, HealthDueAction.ActivateScheme)))
                         .Concat((work.MembershipsToSuspend ?? []).Select(id => (id, HealthDueAction.SuspendForArrears)))
                         .Concat((work.TerminationsDue ?? []).Select(id => (id, HealthDueAction.RaiseTerminationDue)))
                         .Concat((work.SchemesToNotice ?? []).Select(id => (id, HealthDueAction.NoticeSchemeRenewal))))
            {
                await using var scope = Scope(tenantId);
                var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RunHealthDue(id, action, day), cancellationToken);
                if (result.IsSuccess)
                {
                    done++;
                }
                else if (result.Error.Code != BuildingBlocks.Domain.ErrorCodes.InvalidStateTransition)
                {
                    logger.LogWarning("Health job {Action} on {Id} failed: {Code}", action, id, result.Error.Code);
                }
            }

            done += await DeclineHeldClaims(tenantId, day, cancellationToken);
            done += await MeasureProviders(tenantId, day, cancellationToken);
            done += await CareDue(tenantId, day, cancellationToken);
            done += await RecoveriesDue(tenantId, day, cancellationToken);
        }

        return done;
    }

    /// <summary>F037, F038: overdue care activities each raise one task; referrals past their date expire.</summary>
    /// <summary>OQ-139: a recovery uncollected for the tenant's period goes to a person.</summary>
    private async Task<int> RecoveriesDue(Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> due;
        await using (var scope = Scope(tenantId))
        {
            var found = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new FindRecoveriesDue(day, 500), cancellationToken);
            if (found.IsFailure)
            {
                return 0;
            }

            due = found.Value;
        }

        var done = 0;
        foreach (var id in due)
        {
            await using var scope = Scope(tenantId);
            done += (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new SendRecoveryToReview(id), cancellationToken)).IsSuccess ? 1 : 0;
        }

        return done;
    }

    private async Task<int> CareDue(Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        (IReadOnlyList<Guid> Cases, IReadOnlyList<Guid> Referrals) work;
        await using (var scope = Scope(tenantId))
        {
            var found = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new FindCareDueWork(day, 500), cancellationToken);
            if (found.IsFailure)
            {
                return 0;
            }

            work = found.Value;
        }

        var done = 0;
        foreach (var id in work.Cases)
        {
            await using var scope = Scope(tenantId);
            var raised = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RaiseOverdueCareActivities(id, day), cancellationToken);
            done += raised.IsSuccess ? 1 : 0;
        }

        foreach (var id in work.Referrals)
        {
            await using var scope = Scope(tenantId);
            var expired = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ExpireClinicalReferral(id, day), cancellationToken);
            done += expired.IsSuccess ? 1 : 0;
        }

        return done;
    }

    /// <summary>F028: last month's measures, for each provider with claims in it and no results yet; a provider that fails is tried again tomorrow.</summary>
    private async Task<int> MeasureProviders(Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        var monthKey = day.AddMonths(-1).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        IReadOnlyList<string> providers;
        await using (var scope = Scope(tenantId))
        {
            var found = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new FindProvidersToMeasure(monthKey), cancellationToken);
            if (found.IsFailure)
            {
                return 0;
            }

            providers = found.Value;
        }

        var done = 0;
        foreach (var provider in providers)
        {
            await using var scope = Scope(tenantId);
            var measured = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new MeasureProvider(provider, monthKey), cancellationToken);
            if (measured.IsSuccess)
            {
                done++;
            }
            else
            {
                logger.LogWarning("Provider {Provider}'s measures for {Month} failed: {Code}", provider, monthKey, measured.Error.Code);
            }
        }

        return done;
    }

    /// <summary>F006: claims held for arrears are declined in the same run as their membership's suspension, which the run has just recorded.</summary>
    private async Task<int> DeclineHeldClaims(Guid tenantId, DateOnly day, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> claims;
        await using (var scope = Scope(tenantId))
        {
            var found = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new FindHeldClaimsToDecline(500), cancellationToken);
            if (found.IsFailure)
            {
                return 0;
            }

            claims = found.Value;
        }

        var done = 0;
        foreach (var id in claims)
        {
            await using var scope = Scope(tenantId);
            var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new RunHealthDue(id, HealthDueAction.DeclineHeldClaim, day), cancellationToken);
            if (result.IsSuccess)
            {
                done++;
            }
            else if (result.Error.Code != BuildingBlocks.Domain.ErrorCodes.InvalidStateTransition)
            {
                logger.LogWarning("Health job {Action} on {Id} failed: {Code}", HealthDueAction.DeclineHeldClaim, id, result.Error.Code);
            }
        }

        return done;
    }

    /// <summary>F006: claims held for arrears that were paid are adjudicated again, each in its own transaction.</summary>
    private async Task<int> ReleaseHeldClaims(Guid tenantId, IReadOnlyList<Guid> claims, CancellationToken cancellationToken)
    {
        var done = 0;
        foreach (var id in claims)
        {
            await using var scope = Scope(tenantId);
            var released = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new ReviewPendedClaim(id, []), cancellationToken);
            if (released.IsSuccess)
            {
                done++;
            }
            else
            {
                logger.LogWarning("Health job release of held claim {Id} failed: {Code}", id, released.Error.Code);
            }
        }

        return done;
    }

    private AsyncServiceScope Scope(Guid tenantId)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(tenantId, ServiceName);
        return scope;
    }
}

public sealed class HealthJobsService(HealthJobs jobs, IOptions<HealthJobOptions> options, ILogger<HealthJobsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await jobs.Run(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Health jobs failed: {ExceptionType}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
